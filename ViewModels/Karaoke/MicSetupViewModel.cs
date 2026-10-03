using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Singularity.Configuration;
using Singularity.Karaoke;
using Singularity.Karaoke.Audio;
using Singularity.Karaoke.Pitch;
using Singularity.Services.Karaoke;
using Singularity.Views;

namespace Singularity.ViewModels.Karaoke;

public sealed record MicDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Mic setup for up to two singers: each picks a device and a channel (Mix for an ordinary mic,
/// Left/Right for the two sides of a two-mic karaoke adapter). The selected player's input is
/// monitored live (level, recognised note, pitch trail), and the click test measures its latency.
/// Isolates "does the mic work" from anything chart- or timing-related.
/// </summary>
public sealed class MicSetupViewModel : ReactiveObject, IDisposable
{
    /// <summary>Seconds of pitch history the trail shows.</summary>
    public const double TrailSeconds = 4;

    private readonly MicrophoneCapture _mic;
    private readonly LatencyCalibrationRunner _calibration;
    private readonly ConfigManager _configManager;
    private readonly AppConfig _config;
    private readonly ILogger<MicSetupViewModel> _logger;
    private readonly Stopwatch _clock = new();
    private readonly object _sync = new();
    private readonly Queue<(double TimeMs, double? Midi)> _trail = new();
    private PitchStream? _stream;
    private MicChannel _monitoredChannel;
    private float[] _mono = Array.Empty<float>();
    private PitchEstimate _latest;
    private DispatcherTimer? _refresh;

    private int _player = 1;
    private double _levelDb = -90;
    private string _noteText = "–";
    private string _detailText = "";
    private bool _isVoiced;
    private bool _isCalibrating;
    private string _calibrationText = "";
    private double _latencyMs;
    private string _status = "";

    public MicSetupViewModel(MicrophoneCapture mic, LatencyCalibrationRunner calibration, ConfigManager configManager, AppConfig config,
        ILogger<MicSetupViewModel> logger)
    {
        _mic = mic;
        _calibration = calibration;
        _configManager = configManager;
        _config = config;
        _logger = logger;
        _latencyMs = config.KaraokeMicLatencyMs;
        CalibrateCommand = new AsyncRelayCommand(CalibrateAsync, () => !IsCalibrating);
        _mic.SamplesCaptured += OnSamples;
    }

    public List<MicDevice> Devices { get; private set; } = new();
    public IReadOnlyList<MicChannel> Channels { get; } = Enum.GetValues<MicChannel>();

    // ── Players ────────────────────────────────────────────────────────────

    public bool IsPlayer1 { get => _player == 1; set { if (value) SelectPlayer(1); } }
    public bool IsPlayer2 { get => _player == 2; set { if (value) SelectPlayer(2); } }

    public bool Player2Enabled
    {
        get => _config.KaraokeMic2Enabled;
        set
        {
            if (_config.KaraokeMic2Enabled == value) return;
            _config.KaraokeMic2Enabled = value;
            Save();
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(PlayerSettingsEnabled));
        }
    }

    /// <summary>Player 2's device and channel only matter once player 2 is enabled.</summary>
    public bool PlayerSettingsEnabled => _player == 1 || Player2Enabled;

    public MicDevice? SelectedDevice
    {
        get => Devices.FirstOrDefault(d => d.Id == CurrentDeviceId) ?? Devices.FirstOrDefault();
        set
        {
            if (value is null || value.Id == CurrentDeviceId) return;
            if (_player == 1) _config.KaraokeMicDeviceId = value.Id;
            else _config.KaraokeMic2DeviceId = value.Id;
            Save();
            this.RaisePropertyChanged();
            StartMonitoring();
        }
    }

    public MicChannel SelectedChannel
    {
        get => MicAssignment.Parse(_player == 1 ? _config.KaraokeMicChannel : _config.KaraokeMic2Channel);
        set
        {
            if (value == SelectedChannel) return;
            if (_player == 1) _config.KaraokeMicChannel = value.ToString();
            else _config.KaraokeMic2Channel = value.ToString();
            Save();
            this.RaisePropertyChanged();
            StartMonitoring();
        }
    }

    private string CurrentDeviceId => (_player == 1 ? _config.KaraokeMicDeviceId : _config.KaraokeMic2DeviceId) ?? "";

    private MicAssignment Current => new(_player, SelectedDevice?.Id ?? "", SelectedChannel);

    private void SelectPlayer(int player)
    {
        if (_player == player) return;
        _player = player;
        foreach (var name in new[] { nameof(IsPlayer1), nameof(IsPlayer2), nameof(SelectedDevice), nameof(SelectedChannel), nameof(PlayerSettingsEnabled) })
            this.RaisePropertyChanged(name);
        StartMonitoring();
    }

    // ── Live readout ───────────────────────────────────────────────────────

    /// <summary>Input level, dBFS.</summary>
    public double LevelDb { get => _levelDb; private set => this.RaiseAndSetIfChanged(ref _levelDb, value); }

    /// <summary>0..1 over -70..0 dBFS, for the meter.</summary>
    public double LevelFraction => Math.Clamp((LevelDb + 70) / 70, 0, 1);

    public string NoteText { get => _noteText; private set => this.RaiseAndSetIfChanged(ref _noteText, value); }
    public string DetailText { get => _detailText; private set => this.RaiseAndSetIfChanged(ref _detailText, value); }
    public bool IsVoiced { get => _isVoiced; private set => this.RaiseAndSetIfChanged(ref _isVoiced, value); }

    public bool IsCalibrating
    {
        get => _isCalibrating;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isCalibrating, value);
            (CalibrateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string CalibrationText { get => _calibrationText; private set => this.RaiseAndSetIfChanged(ref _calibrationText, value); }

    /// <summary>Saved input latency, ms (shared by both players); editable by hand as well as by the click test.</summary>
    public double LatencyMs
    {
        get => _latencyMs;
        set
        {
            value = Math.Clamp(Math.Round(value), 0, 500);
            this.RaiseAndSetIfChanged(ref _latencyMs, value);
            _config.KaraokeMicLatencyMs = value;
            Save();
        }
    }

    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public ICommand CalibrateCommand { get; }

    /// <summary>The pitch trail for the last <see cref="TrailSeconds"/>: (seconds ago, MIDI or null for silence).</summary>
    public IReadOnlyList<(double SecondsAgo, double? Midi)> Trail
    {
        get
        {
            lock (_sync)
            {
                double now = _clock.Elapsed.TotalMilliseconds;
                return _trail.Select(t => ((now - t.TimeMs) / 1000, t.Midi)).ToList();
            }
        }
    }

    /// <summary>Raised ~30 times a second while monitoring, after the readout properties update.</summary>
    public event Action? Refreshed;

    /// <summary>Called when the page is shown.</summary>
    public void Activate()
    {
        try
        {
            Devices = MicrophoneCapture.ListDevices().Select(d => new MicDevice(d.Id, d.Name)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing microphones failed");
            Devices = new();
        }
        this.RaisePropertyChanged(nameof(Devices));
        this.RaisePropertyChanged(nameof(SelectedDevice));
        StartMonitoring();
    }

    /// <summary>Called when the page is hidden: releases the microphone.</summary>
    public void Deactivate()
    {
        _refresh?.Stop();
        _refresh = null;
        if (!IsCalibrating) _mic.Stop();
        lock (_sync) _stream = null;
    }

    private void Save() => _ = _configManager.SaveAsync(_config);

    private void StartMonitoring()
    {
        if (IsCalibrating) return;
        _clock.Restart();
        var current = Current;
        if (!_mic.Start(() => _clock.Elapsed.TotalMilliseconds, current.DeviceId))
        {
            Status = Devices.Count == 0 ? "No microphone found." : "Couldn't open this microphone.";
            return;
        }
        lock (_sync)
        {
            _trail.Clear();
            _monitoredChannel = current.Channel;
            _stream = new PitchStream(_mic.SampleRate, SingerSession.SilenceDb);
            _stream.Frame += OnFrame;
        }
        Status = $"Player {_player} listening: {_mic.DeviceName} ({_mic.SampleRate / 1000.0:0.#} kHz, {_mic.Channels} ch, {current.Channel})";
        _refresh ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Refresh());
        _refresh.Start();
    }

    private void OnSamples(MicBlock block)
    {
        lock (_sync)
        {
            if (_stream is null) return;
            if (_mono.Length < block.Frames) _mono = new float[block.Frames];
            int count = block.Extract(_monitoredChannel, _mono);
            _stream.Push(_mono.AsSpan(0, count), block.TimeMs);
        }
    }

    private void OnFrame(double timeMs, PitchEstimate estimate)
    {
        // Called under _sync from OnSamples.
        _latest = estimate;
        _trail.Enqueue((timeMs, estimate.IsVoiced ? estimate.Midi : null));
        while (_trail.Count > 0 && timeMs - _trail.Peek().TimeMs > TrailSeconds * 1000) _trail.Dequeue();
    }

    private void Refresh()
    {
        PitchEstimate p;
        lock (_sync) p = _latest;
        LevelDb = p.LevelDb;
        this.RaisePropertyChanged(nameof(LevelFraction));
        IsVoiced = p.IsVoiced;
        if (p.IsVoiced)
        {
            NoteText = NoteNames.Name(p.Midi);
            double cents = NoteNames.Cents(p.Midi);
            DetailText = $"{p.Hz:0} Hz · {(cents >= 0 ? "+" : "")}{cents:0} cents · clarity {p.Clarity:0.00}";
        }
        else
        {
            NoteText = "–";
            DetailText = p.LevelDb < SingerSession.SilenceDb ? $"{p.LevelDb:0} dBFS: too quiet, below the silence gate" : $"{p.LevelDb:0} dBFS: no clear pitch";
        }
        Refreshed?.Invoke();
    }

    private async Task CalibrateAsync()
    {
        var mic = Current;
        Deactivate();
        IsCalibrating = true;
        CalibrationText = "Playing clicks… keep the room quiet; use speakers, not headphones.";
        try
        {
            var estimate = await _calibration.RunAsync(mic);
            if (estimate.IsReliable)
            {
                LatencyMs = estimate.LatencyMs;
                CalibrationText = $"Measured {estimate.LatencyMs:0} ms (±{estimate.SpreadMs:0} ms, {estimate.ClicksHeard}/{estimate.ClicksPlayed} clicks). Saved.";
            }
            else
            {
                CalibrationText = estimate.ClicksHeard == 0
                    ? "The microphone didn't hear the clicks. Turn the speakers up or move the mic closer. If that doesn't help, switch off "
                      + "\"Audio enhancements\" for this microphone in Windows Sound settings: its echo cancellation can filter out the clicks."
                    : $"Unreliable result ({estimate.ClicksHeard}/{estimate.ClicksPlayed} clicks, ±{estimate.SpreadMs:0} ms); nothing saved. Try again in a quieter room.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Latency calibration failed");
            CalibrationText = "Calibration failed: " + ex.Message;
        }
        finally
        {
            IsCalibrating = false;
            StartMonitoring();
        }
    }

    public void Dispose()
    {
        _mic.SamplesCaptured -= OnSamples;
        Deactivate();
    }
}
