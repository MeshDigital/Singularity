using System;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Singularity.Services.Audio;

namespace Singularity.Services.Karaoke;

/// <summary>
/// Plays one song for the sing screen and is its master clock. <see cref="PositionMs"/> comes from
/// the output device's own audio clock (WASAPI's IAudioClock via <see cref="IWavePosition"/>): the
/// samples the speakers have actually played, not the ones decoded or queued. Notes, lyrics and mic
/// readings are all placed on this clock, so UI frame timing never moves anything.
/// Separate from the library player on purpose: no queue, crossfade or normalisation in the way.
/// </summary>
public sealed class SingAudioEngine : IDisposable
{
    private readonly ILogger<SingAudioEngine> _logger;
    private WasapiOut? _output;
    private AudioFileReader? _reader;
    private double _baseMs; // song position where the device clock last restarted from zero
    private AudioFileReader? _vocalsReader;
    private VolumeSampleProvider? _vocals;
    private float _vocalsVolume;

    public SingAudioEngine(ILogger<SingAudioEngine> logger) => _logger = logger;

    public bool IsLoaded => _output is not null;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public double DurationMs => _reader?.TotalTime.TotalMilliseconds ?? 0;

    /// <summary>Raised when playback reaches the end of the file (on an NAudio thread).</summary>
    public event Action? Ended;

    /// <summary>Song position the listener is hearing now, in ms.</summary>
    public double PositionMs
    {
        get
        {
            var output = _output;
            if (output is null) return 0;
            var position = (IWavePosition)output;
            double played = position.GetPosition() * 1000.0 / position.OutputWaveFormat.AverageBytesPerSecond;
            return _baseMs + played;
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> on the default output device, ready to play from <paramref name="startMs"/>.
    /// With <paramref name="stems"/> it plays the instrumental and the vocals instead, mixed live, so the
    /// original vocals can be turned down (<see cref="VocalsVolume"/>) without touching the timing.
    /// </summary>
    public void Load(string path, double startMs = 0, (string Vocals, string Instrumental)? stems = null)
    {
        Stop();
        _vocals = null;
        _vocalsReader = null;
        _reader = PlayableAudio.Open(stems?.Instrumental ?? path, out var playable);
        _reader.CurrentTime = TimeSpan.FromMilliseconds(Math.Max(0, startMs));
        _baseMs = _reader.CurrentTime.TotalMilliseconds;

        ISampleProvider source = _reader;
        if (stems is { } st)
        {
            _vocalsReader = PlayableAudio.Open(st.Vocals, out _);
            _vocalsReader.CurrentTime = _reader.CurrentTime;
            ISampleProvider vocals = _vocalsReader;
            // Both stems come from the same separation, but be safe: match the instrumental's format.
            if (vocals.WaveFormat.Channels != source.WaveFormat.Channels)
                vocals = vocals.WaveFormat.Channels == 1 ? new MonoToStereoSampleProvider(vocals) : new StereoToMonoSampleProvider(vocals);
            if (vocals.WaveFormat.SampleRate != source.WaveFormat.SampleRate)
                vocals = new WdlResamplingSampleProvider(vocals, source.WaveFormat.SampleRate);
            _vocals = new VolumeSampleProvider(vocals) { Volume = _vocalsVolume };
            var mixer = new MixingSampleProvider(new[] { source, _vocals }) { ReadFully = false };
            source = mixer;
        }

        // Shared mode, 50 ms buffer: low enough that pause/seek feel immediate, safe on any device.
        _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 50);
        _output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null) _logger.LogWarning(e.Exception, "Sing playback stopped with an error");
            else if (_reader is { } r && r.Position >= r.Length) Ended?.Invoke();
        };
        _output.Init(source);
        _logger.LogInformation("Sing audio loaded: {File} ({Seconds:0.0}s){Stems}", playable, DurationMs / 1000,
            stems is null ? "" : " with separated vocals");
    }

    /// <summary>True when the song plays as instrumental + vocals stems, so the vocals can be turned down.</summary>
    public bool HasStems => _vocals is not null;

    /// <summary>Original vocals volume, 0 (real karaoke) to 1; only effective with stems.</summary>
    public float VocalsVolume
    {
        get => _vocalsVolume;
        set
        {
            _vocalsVolume = Math.Clamp(value, 0f, 1f);
            if (_vocals is { } v) v.Volume = _vocalsVolume;
        }
    }

    public void Play() => _output?.Play();

    public void Pause() => _output?.Pause();

    public void Stop()
    {
        var output = _output;
        _output = null;
        output?.Stop();
        output?.Dispose();
        _reader?.Dispose();
        _reader = null;
        _vocalsReader?.Dispose();
        _vocalsReader = null;
        _vocals = null;
        _baseMs = 0;
    }

    public void Dispose() => Stop();
}
