using Singularity.Contracts.UltraStar;
using Singularity.Karaoke;
using Singularity.Karaoke.Scoring;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class SingerSessionTests
{
    private const int Rate = 48_000;

    // 150 BPM grid: 100 ms per beat. Two lines of half-second notes.
    private static readonly UltraStarSong Song = new()
    {
        Title = "t", Artist = "a", AudioFile = "a.mp3", Bpm = 150, GapMs = 500,
        Voices = new[]
        {
            new UltraStarVoice(new[]
            {
                new UltraStarNote(NoteType.Regular, 0, 5, 57, "la"),
                new UltraStarNote(NoteType.Regular, 6, 5, 60, " la"),
                UltraStarNote.LineBreak(12),
                new UltraStarNote(NoteType.Golden, 14, 6, 64, "laa"),
            }),
        },
    };

    /// <summary>Renders a singer: each note's pitch from <paramref name="startShiftMs"/> late, as audio of the whole song.</summary>
    private static float[] Perform(Func<UltraStarNote, double?> pitchFor, double startShiftMs = 0, double lengthMs = 3000)
    {
        var audio = new float[(int)(Rate * lengthMs / 1000)];
        foreach (var n in Song.Voices[0].Notes.Where(n => n.Type != NoteType.LineBreak))
        {
            if (pitchFor(n) is not { } midi) continue;
            double hz = 440 * Math.Pow(2, (midi - 69) / 12);
            int a = (int)((Song.BeatToMs(n.StartBeat) + startShiftMs) * Rate / 1000);
            int b = Math.Min(audio.Length, (int)((Song.BeatToMs(n.StartBeat + n.DurationBeats) + startShiftMs) * Rate / 1000));
            for (int i = a; i < b; i++)
                audio[i] = (float)(0.3 * Math.Sin(2 * Math.PI * hz * i / Rate) + 0.15 * Math.Sin(4 * Math.PI * hz * i / Rate));
        }
        return audio;
    }

    /// <summary>Pushes the audio in device-sized chunks (10 ms), as WASAPI would.</summary>
    private static SingerSession Run(float[] audio, double latencyMs = 0)
    {
        var session = new SingerSession(Song, 0, Rate, Difficulty.Medium, latencyMs);
        const int chunk = Rate / 100;
        for (int i = 0; i < audio.Length; i += chunk)
            session.Push(audio.AsSpan(i, Math.Min(chunk, audio.Length - i)), i * 1000.0 / Rate);
        session.Finish();
        return session;
    }

    [Fact]
    public void PerfectPerformance_ScoresNearlyEverything()
    {
        var s = Run(Perform(n => n.MidiTone));
        Assert.True(s.Scorer.Score.Total >= 9_000, $"scored {s.Scorer.Score.Total}");
    }

    [Fact]
    public void Silence_ScoresNothing() => Assert.Equal(0, Run(new float[Rate * 3]).Scorer.Score.Total);

    [Fact]
    public void WrongNotes_ScoreNothing() => Assert.Equal(0, Run(Perform(n => n.MidiTone + 4)).Scorer.Score.Total);

    [Fact]
    public void MicLatency_IsCompensated()
    {
        var late = Perform(n => n.MidiTone, startShiftMs: 250); // the audio reaches us 250 ms late
        int uncompensated = Run(late).Scorer.Score.Total;
        int compensated = Run(late, latencyMs: 250).Scorer.Score.Total;

        Assert.True(compensated >= 9_000, $"compensated {compensated}");
        Assert.True(uncompensated < compensated - 2_000, $"uncompensated {uncompensated} vs {compensated}");
    }

    [Fact]
    public void Readings_AreReportedOnTheBeatTimeline()
    {
        var session = new SingerSession(Song, 0, Rate);
        var readings = new List<PitchReading>();
        session.PitchDetected += readings.Add;
        var audio = Perform(n => n.MidiTone);
        session.Push(audio, 0);

        Assert.InRange(readings.Count, 280, 300); // ~3 s at one reading per 10 ms, minus the first frame
        var duringFirstNote = readings.Where(r => r.Beat is > 1 and < 4).ToList();
        Assert.All(duringFirstNote, r => Assert.Equal(57, r.Pitch.Midi, 0));
    }

    [Fact]
    public void SeekingRestartsTheBuffer()
    {
        var session = new SingerSession(Song, 0, Rate);
        var readings = new List<PitchReading>();
        session.PitchDetected += readings.Add;
        var audio = Perform(n => n.MidiTone);

        session.Push(audio.AsSpan(0, Rate / 2), 0);
        readings.Clear();
        session.Push(audio.AsSpan(Rate * 2, Rate / 2), 2000); // jumped 1.5 s ahead
        Assert.All(readings, r => Assert.True(Song.BeatToMs(r.Beat) >= 2000, $"reading at {Song.BeatToMs(r.Beat)} ms"));
    }
}
