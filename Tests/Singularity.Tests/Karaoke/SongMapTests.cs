using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Display;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class SongMapTests
{
    // 150 BPM: 100 ms per beat, no gap.
    private static UltraStarSong Song(params UltraStarVoice[] voices) => new()
    {
        Title = "t", Artist = "a", AudioFile = "a.mp3", Bpm = 150, GapMs = 0, Voices = voices,
    };

    private static UltraStarNote Note(int start, int length, NoteType type = NoteType.Regular) => new(type, start, length, 60, "la");

    [Fact]
    public void ShortPauses_AreOneStretch_LongOnesSplit()
    {
        // Notes at 0-0.4 s and 1.0-1.4 s (0.6 s apart: merged), then 10-10.4 s (a long break: its own stretch).
        var song = Song(new UltraStarVoice(new[] { Note(0, 4), Note(10, 4), Note(100, 4) }));

        var spans = SongMap.For(song, 20_000).Voices[0];

        Assert.Equal(new[] { new TimeSpanMs(0, 1400), new TimeSpanMs(10_000, 10_400) }, spans);
    }

    [Fact]
    public void EachDuetVoice_HasItsOwnStretches()
    {
        var song = Song(new UltraStarVoice(new[] { Note(0, 10) }), new UltraStarVoice(new[] { Note(50, 10) }));
        var map = SongMap.For(song, 10_000);

        Assert.Equal(2, map.Voices.Count);
        Assert.NotNull(SongMap.At(map.Voices[1], 5_500));
        Assert.Null(SongMap.At(map.Voices[0], 5_500));
    }

    private const int Rate = 8000;

    /// <summary>Vocals: a loud tone in each of the given stretches (ms), near-silence elsewhere.</summary>
    private static float[] Vocals(int seconds, params (int From, int To)[] singing)
    {
        var v = new float[seconds * Rate];
        var rng = new Random(3);
        for (int i = 0; i < v.Length; i++) v[i] = (float)((rng.NextDouble() - 0.5) * 0.0005);
        foreach (var (from, to) in singing)
            for (int i = from * Rate / 1000; i < to * Rate / 1000; i++) v[i] += (float)(0.3 * Math.Sin(i * 0.2));
        return v;
    }

    [Fact]
    public void SingingWithoutNotes_IsUnscored_SingingOnNotesIsNot()
    {
        // Notes 1-3 s; the singer also sings 6-9 s (an ad-lib with no notes) and 12-12.5 s (too short to show).
        var song = Song(new UltraStarVoice(new[] { Note(10, 20) }));
        var vocals = Vocals(15, (1000, 3000), (6000, 9000), (12_000, 12_500));

        var unscored = SongMap.FindUnscoredVocals(song, vocals, Rate);

        var only = Assert.Single(unscored);
        Assert.InRange(only.FromMs, 5900, 6100);
        Assert.InRange(only.ToMs, 8900, 9100);
    }

    [Fact]
    public void AnotherVoicesNotes_CountAsCharted()
    {
        // The singing at 6-9 s is the duet partner's part: charted, so not "unscored".
        var song = Song(new UltraStarVoice(new[] { Note(10, 20) }), new UltraStarVoice(new[] { Note(60, 30) }));
        Assert.Empty(SongMap.FindUnscoredVocals(song, Vocals(15, (1000, 3000), (6000, 9000)), Rate));
    }
}
