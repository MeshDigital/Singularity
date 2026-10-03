using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Singularity.Karaoke.Audio;
using Singularity.Karaoke.Calibration;

namespace Singularity.Services.Karaoke;

/// <summary>
/// Runs the click test: plays <see cref="LatencyCalibrator.CreateClickTrack"/> through the speakers
/// while recording the microphone. Mic blocks are dated with the output device's played-sample clock,
/// the same clock the game uses, so the measured delay is exactly what the game must subtract from
/// mic readings (<see cref="Singularity.Configuration.AppConfig.KaraokeMicLatencyMs"/>).
/// Needs speakers, not headphones: the microphone has to hear the clicks.
/// </summary>
public sealed class LatencyCalibrationRunner
{
    private const int TrackSampleRate = 48_000;

    private readonly MicrophoneCapture _mic;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<LatencyCalibrationRunner> _logger;

    public LatencyCalibrationRunner(MicrophoneCapture mic, ILoggerFactory loggers)
    {
        _mic = mic;
        _loggers = loggers;
        _logger = loggers.CreateLogger<LatencyCalibrationRunner>();
    }

    /// <summary>Measures the latency of one singer's microphone (device and channel).</summary>
    public async Task<LatencyEstimate> RunAsync(MicAssignment mic, CancellationToken ct = default)
    {
        var (track, clicks) = LatencyCalibrator.CreateClickTrack(TrackSampleRate);
        var wav = Path.Combine(Path.GetTempPath(), $"singularity-clicks-{Guid.NewGuid():N}.wav");
        using (var writer = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(TrackSampleRate, 1)))
            writer.WriteSamples(track, 0, track.Length);

        using var output = new SingAudioEngine(_loggers.CreateLogger<SingAudioEngine>());
        float[]? recorded = null;
        int micRate = 0;
        float[] mono = Array.Empty<float>();
        void OnSamples(MicBlock block)
        {
            var buffer = recorded;
            if (buffer is null) return;
            if (mono.Length < block.Frames) mono = new float[block.Frames];
            int count = block.Extract(mic.Channel, mono);
            var samples = mono;
            // Place each block where it belongs on the click track's timeline.
            int at = (int)Math.Round(block.TimeMs * micRate / 1000);
            for (int i = 0; i < count; i++)
            {
                int j = at + i;
                if (j >= 0 && j < buffer.Length) buffer[j] = samples[i];
            }
        }

        try
        {
            output.Load(wav);
            if (!_mic.Start(() => output.PositionMs, mic.DeviceId))
                return new LatencyEstimate(0, double.MaxValue, 0, clicks.Length);
            micRate = _mic.SampleRate;
            recorded = new float[(int)((long)track.Length * micRate / TrackSampleRate)];
            _mic.SamplesCaptured += OnSamples;
            output.Play();
            await Task.Delay(TimeSpan.FromMilliseconds(track.Length * 1000.0 / TrackSampleRate + 300), ct);
        }
        finally
        {
            _mic.SamplesCaptured -= OnSamples;
            _mic.Stop();
            output.Stop();
            try { File.Delete(wav); } catch (IOException) { }
        }

        var estimate = LatencyCalibrator.Estimate(recorded, micRate, clicks);
        _logger.LogInformation("Mic latency calibration: {Latency:0} ms, spread {Spread:0.0} ms, {Heard}/{Played} clicks heard",
            estimate.LatencyMs, estimate.SpreadMs, estimate.ClicksHeard, estimate.ClicksPlayed);
        return estimate;
    }
}
