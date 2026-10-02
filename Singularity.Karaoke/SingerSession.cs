using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Pitch;
using Singularity.Karaoke.Scoring;

namespace Singularity.Karaoke;

/// <summary>A pitch reading placed on the song's timeline, for the live pitch display.</summary>
public readonly record struct PitchReading(double Beat, PitchEstimate Pitch);

/// <summary>
/// One singer on one voice: microphone audio in, score out. Audio is pushed as the device delivers
/// it, together with the song position of its first sample. Every <see cref="HopMs"/> a frame is
/// analysed. Its centre, moved earlier by the mic's latency, says which beat the singer was singing,
/// and the reading goes to the <see cref="SingScorer"/>. Device code (WASAPI capture, clock
/// conversion) stays in the app, so this class is fully testable with synthetic audio.
/// </summary>
public sealed class SingerSession
{
    public const double HopMs = 10;

    private readonly UltraStarSong _song;
    private readonly PitchDetector _detector;
    private readonly int _sampleRate;
    private readonly int _hop;
    private readonly float[] _buffer;
    private int _buffered;
    private double _bufferStartMs = double.NaN;

    public SingerSession(UltraStarSong song, int voiceIndex, int sampleRate, Difficulty difficulty = Difficulty.Medium, double latencyMs = 0)
    {
        _song = song;
        _sampleRate = sampleRate;
        _detector = new PitchDetector(sampleRate, frameSize: FrameSizeFor(sampleRate));
        _hop = (int)Math.Round(sampleRate * HopMs / 1000);
        _buffer = new float[_detector.FrameSize * 4];
        LatencyMs = latencyMs;
        Scorer = new SingScorer(song.Voices[voiceIndex], difficulty);
    }

    public SingScorer Scorer { get; }

    /// <summary>Time from the singer's voice to the samples reaching <see cref="Push"/>; set by calibration.</summary>
    public double LatencyMs { get; set; }

    /// <summary>The most recent reading, for the pitch arrow next to the notes.</summary>
    public PitchReading? LastReading { get; private set; }

    public event Action<PitchReading>? PitchDetected;

    /// <param name="samples">Mono samples in -1..1, contiguous with the previous push.</param>
    /// <param name="songTimeMs">Song position (as <see cref="UltraStarSong.BeatToMs"/> counts it) of <paramref name="samples"/>[0].</param>
    public void Push(ReadOnlySpan<float> samples, double songTimeMs)
    {
        // A gap or jump (seek, pause, device restart) starts a fresh buffer at the new position.
        double expected = _bufferStartMs + _buffered * 1000.0 / _sampleRate;
        if (double.IsNaN(_bufferStartMs) || Math.Abs(expected - songTimeMs) > 50)
        {
            _buffered = 0;
            _bufferStartMs = songTimeMs;
        }

        while (samples.Length > 0)
        {
            int take = Math.Min(samples.Length, _buffer.Length - _buffered);
            samples[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            samples = samples[take..];
            AnalyseBufferedFrames();
        }
    }

    /// <summary>Judges what is still pending; call when the song ends.</summary>
    public void Finish() => Scorer.Finish();

    private void AnalyseBufferedFrames()
    {
        int frame = _detector.FrameSize;
        int offset = 0;
        while (_buffered - offset >= frame)
        {
            var estimate = _detector.Detect(_buffer.AsSpan(offset, frame));
            double centreMs = _bufferStartMs + (offset + frame / 2.0) * 1000.0 / _sampleRate - LatencyMs;
            var reading = new PitchReading(_song.MsToBeat(centreMs), estimate);
            LastReading = reading;
            Scorer.AddSample(reading.Beat, estimate.IsVoiced ? estimate.Midi : null);
            PitchDetected?.Invoke(reading);
            offset += _hop;
        }
        if (offset == 0) return;
        Array.Copy(_buffer, offset, _buffer, 0, _buffered - offset);
        _buffered -= offset;
        _bufferStartMs += offset * 1000.0 / _sampleRate;
    }

    /// <summary>About 43 ms of audio, rounded to a power of two: low enough for bass voices, short enough to follow fast notes.</summary>
    private static int FrameSizeFor(int sampleRate) => 1 << (int)Math.Round(Math.Log2(sampleRate * 0.043));
}
