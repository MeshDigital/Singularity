using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Sync;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class ChartSyncTests
{
    private const int Rate = 8000;

    /// <summary>A three-minute chart: phrases of irregular notes with pauses between lines.</summary>
    internal static UltraStarSong Chart(int seed = 4, int gapMs = 5000)
    {
        var rng = new Random(seed);
        var notes = new List<UltraStarNote>();
        int beat = 0;
        while (beat < 3000)
        {
            int count = 4 + rng.Next(6);
            for (int i = 0; i < count; i++)
            {
                int length = 2 + rng.Next(8);
                notes.Add(new UltraStarNote(NoteType.Regular, beat, length, rng.Next(12), "la"));
                beat += length + rng.Next(3);
            }
            notes.Add(UltraStarNote.LineBreak(beat + 2));
            beat += 12 + rng.Next(30);
        }
        return new UltraStarSong { Title = "T", Artist = "A", Bpm = 300, GapMs = gapMs, AudioFile = "a.mp3", Voices = new[] { new UltraStarVoice(notes) } };
    }

    /// <summary>A vocal stem for the chart: voice-like noise while a note sounds, <paramref name="shiftMs"/> later than the chart says.</summary>
    /// <summary>A singer singing <paramref name="chart"/>'s notes (in the octave above middle C's below), <paramref name="shiftMs"/> late.</summary>
    /// <param name="transpose">Sing every note this many semitones off the chart.</param>
    internal static float[] Vocals(UltraStarSong chart, int shiftMs, int seconds = 200, int seed = 8, int transpose = 0)
    {
        var rng = new Random(seed);
        var v = new float[seconds * Rate];
        for (int i = 0; i < v.Length; i++) v[i] = (float)((rng.NextDouble() - 0.5) * 0.002);
        foreach (var n in chart.Voices[0].Notes.Where(n => n.Type != NoteType.LineBreak))
        {
            int from = (int)((chart.BeatToMs(n.StartBeat) + shiftMs) * Rate / 1000);
            int to = (int)((chart.BeatToMs(n.StartBeat + n.DurationBeats) + shiftMs) * Rate / 1000);
            double amp = 0.2 + rng.NextDouble() * 0.3;
            int midi = 48 + ((n.MidiTone + transpose) % 12 + 12) % 12; // the test charts use pitch classes 0..11
            double step = 2 * Math.PI * 440 * Math.Pow(2, (midi - 69) / 12.0) / Rate;
            for (int i = Math.Max(0, from); i < Math.Min(v.Length, to); i++) v[i] += (float)(amp * Math.Sin(i * step) * (0.85 + 0.15 * rng.NextDouble()));
        }
        return v;
    }

    internal const int VocalsRate = Rate;

    [Theory]
    [InlineData(0)]
    [InlineData(1730)]
    [InlineData(-940)]
    public void AChart_IsPlacedOnVocalsThatStartLaterOrEarlier(int shiftMs)
    {
        var chart = Chart();

        var result = ChartSync.Measure(chart, Vocals(chart, shiftMs), Rate);

        Assert.True(result.Gap.IsValid, string.Join(", ", result.Windows.Select(w => $"{w.OffsetMs}@{w.Correlation:0.00}")));
        Assert.InRange(result.Gap.VideoGapMs, shiftMs - 20, shiftMs + 20);
    }

    [Fact]
    public void AnotherSongsChart_DoesNotFit() =>
        Assert.False(ChartSync.Measure(Chart(seed: 1), Vocals(Chart(seed: 2), 0), Rate).Gap.IsValid);

    [Fact]
    public void Shift_MovesTheGapAndTheSongTimeHeaders()
    {
        var chart = Chart() with { StartMs = 1000, EndMs = 150_000, PreviewStartMs = 40_000 };

        var moved = ChartSync.Shift(chart, 1730);

        Assert.Equal(6730, moved.GapMs);
        Assert.Equal(2730, moved.StartMs);
        Assert.Equal(151_730, moved.EndMs);
        Assert.Equal(41_730, moved.PreviewStartMs);
        Assert.Equal(0, ChartSync.Shift(chart with { StartMs = 500 }, -900).StartMs);
    }
}
