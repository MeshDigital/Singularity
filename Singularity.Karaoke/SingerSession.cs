using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Pitch;
using Singularity.Karaoke.Scoring;

namespace Singularity.Karaoke;

/// <summary>A pitch reading placed on the song's timeline, for the live pitch display.</summary>
public readonly record struct PitchReading(double Beat, PitchEstimate Pitch);

/// <summary>
/// One singer on one voice: microphone audio in, score out. Audio is pushed as the device delivers
/// it, together with the song position of its first sample. A <see cref="PitchStream"/> analyses a
/// frame every 10 ms. Each frame's centre, moved earlier by the mic's latency, says which beat the
/// singer was singing, and the reading goes to the <see cref="SingScorer"/>. Device code (WASAPI
/// capture, clock conversion) stays in the app, so this class is fully testable with synthetic audio.
/// </summary>
public sealed class SingerSession
{
    public const double HopMs = PitchStream.HopMs;

    /// <summary>-55 dBFS rather than the detector's -45: real microphones are often quieter than test
    /// tones, and the clarity check already rejects room noise.</summary>
    public const double SilenceDb = -55;

    private readonly UltraStarSong _song;
    private readonly PitchStream _stream;

    public SingerSession(UltraStarSong song, int voiceIndex, int sampleRate, Difficulty difficulty = Difficulty.Medium, double latencyMs = 0)
    {
        _song = song;
        LatencyMs = latencyMs;
        Scorer = new SingScorer(song.Voices[voiceIndex], difficulty, beatMs: song.MillisecondsPerBeat);
        _stream = new PitchStream(sampleRate, SilenceDb);
        _stream.Frame += OnFrame;
    }

    public SingScorer Scorer { get; }

    /// <summary>Time from the singer's voice to the samples reaching <see cref="Push"/>; set by calibration.</summary>
    public double LatencyMs { get; set; }

    /// <summary>The most recent reading, for the pitch arrow next to the notes.</summary>
    public PitchReading? LastReading { get; private set; }

    public event Action<PitchReading>? PitchDetected;

    /// <param name="samples">Mono samples in -1..1, contiguous with the previous push.</param>
    /// <param name="songTimeMs">Song position (as <see cref="UltraStarSong.BeatToMs"/> counts it) of <paramref name="samples"/>[0].</param>
    public void Push(ReadOnlySpan<float> samples, double songTimeMs) => _stream.Push(samples, songTimeMs);

    /// <summary>Judges what is still pending; call when the song ends.</summary>
    public void Finish() => Scorer.Finish();

    private void OnFrame(double centreMs, PitchEstimate estimate)
    {
        var reading = new PitchReading(_song.MsToBeat(centreMs - LatencyMs), estimate);
        LastReading = reading;
        Scorer.AddSample(reading.Beat, estimate.IsVoiced ? estimate.Midi : null);
        PitchDetected?.Invoke(reading);
    }
}
