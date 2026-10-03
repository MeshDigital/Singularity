using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Singularity.Karaoke.Audio;

namespace Singularity.Services.Karaoke;

/// <summary>
/// Captures one microphone (the Windows default recording device for now) as mono float samples,
/// each block stamped with the song position of its first sample. The block's last sample arrived
/// just now, so it's dated by the song clock at the moment it is delivered. What remains (the
/// capture buffer and device latency) is a constant that calibration measures.
/// </summary>
public sealed class MicrophoneCapture : IDisposable
{
    private readonly ILogger<MicrophoneCapture> _logger;
    private WasapiCapture? _capture;
    private Func<double>? _songClockMs;
    private float[] _interleaved = Array.Empty<float>();

    public MicrophoneCapture(ILogger<MicrophoneCapture> logger) => _logger = logger;

    public int SampleRate { get; private set; }
    public string? DeviceName { get; private set; }

    public int Channels { get; private set; }

    /// <summary>Each captured block, all channels interleaved, dated by the clock. Raised on the capture thread;
    /// the block's buffer is reused for the next one.</summary>
    public event Action<MicBlock>? SamplesCaptured;

    /// <summary>Active recording devices as (endpoint id, name), the Windows default first.</summary>
    public static IReadOnlyList<(string Id, string Name)> ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID
            : null;
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => (d.ID, d.FriendlyName))
            .OrderByDescending(d => d.ID == defaultId)
            .ThenBy(d => d.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Starts the microphone with endpoint id <paramref name="deviceId"/>, or the Windows default when
    /// it's empty or no longer present. False when there is none (the game then runs without scoring).
    /// </summary>
    public bool Start(Func<double> songClockMs, string? deviceId = null)
    {
        Stop();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;
            if (!string.IsNullOrEmpty(deviceId))
            {
                try { device = enumerator.GetDevice(deviceId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Configured microphone {Id} not found; using the default", deviceId); }
                if (device is { State: not DeviceState.Active }) device = null;
            }
            if (device is null)
            {
                if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
                {
                    _logger.LogWarning("No microphone found; singing won't be scored");
                    return false;
                }
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
            DeviceName = device.FriendlyName;
            _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);
            SampleRate = _capture.WaveFormat.SampleRate;
            Channels = _capture.WaveFormat.Channels;
            _songClockMs = songClockMs;
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) _logger.LogWarning(e.Exception, "Microphone capture stopped with an error");
            };
            _capture.StartRecording();
            _logger.LogInformation("Microphone: {Device} ({Format})", DeviceName, _capture.WaveFormat);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start the microphone; singing won't be scored");
            Stop();
            return false;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        var clock = _songClockMs;
        if (capture is null || clock is null || e.BytesRecorded == 0) return;

        var format = capture.WaveFormat;
        int channels = format.Channels;
        int frames = e.BytesRecorded / format.BlockAlign;
        int samples = frames * channels;
        if (_interleaved.Length < samples) _interleaved = new float[samples];

        // WASAPI's shared-mode mix format is 32-bit float, usually wrapped as WAVE_FORMAT_EXTENSIBLE.
        if (format.BitsPerSample == 32 && format.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
        {
            Buffer.BlockCopy(e.Buffer, 0, _interleaved, 0, samples * 4);
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i < samples; i++) _interleaved[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        }
        else
        {
            return; // 24-bit/32-bit int shared-mode formats are rare; add when a device needs them
        }

        double firstSampleMs = clock() - frames * 1000.0 / format.SampleRate;
        SamplesCaptured?.Invoke(new MicBlock(_interleaved, frames, channels, firstSampleMs));
    }

    public void Stop()
    {
        var capture = _capture;
        _capture = null;
        _songClockMs = null;
        if (capture is null) return;
        capture.DataAvailable -= OnData;
        try { capture.StopRecording(); } catch (Exception ex) { _logger.LogDebug(ex, "Stopping capture"); }
        capture.Dispose();
    }

    public void Dispose() => Stop();
}
