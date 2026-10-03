using System;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

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
    private float[] _mono = Array.Empty<float>();

    public MicrophoneCapture(ILogger<MicrophoneCapture> logger) => _logger = logger;

    public int SampleRate { get; private set; }
    public string? DeviceName { get; private set; }

    /// <summary>Mono samples and the song time (ms) of the first one. Raised on the capture thread.</summary>
    public event Action<float[], int, double>? SamplesCaptured;

    /// <summary>Starts the default microphone. False when there is none (the game then runs without scoring).</summary>
    public bool Start(Func<double> songClockMs)
    {
        Stop();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
            {
                _logger.LogWarning("No microphone found; singing won't be scored");
                return false;
            }
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            DeviceName = device.FriendlyName;
            _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);
            SampleRate = _capture.WaveFormat.SampleRate;
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
        if (_mono.Length < frames) _mono = new float[frames];

        // WASAPI's shared-mode mix format is 32-bit float, usually wrapped as WAVE_FORMAT_EXTENSIBLE.
        if (format.BitsPerSample == 32 && format.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
        {
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += BitConverter.ToSingle(e.Buffer, (f * channels + c) * 4);
                _mono[f] = sum / channels;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(e.Buffer, (f * channels + c) * 2) / 32768f;
                _mono[f] = sum / channels;
            }
        }
        else
        {
            return; // 24-bit/32-bit int shared-mode formats are rare; add when a device needs them
        }

        double firstSampleMs = clock() - frames * 1000.0 / format.SampleRate;
        SamplesCaptured?.Invoke(_mono, frames, firstSampleMs);
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
