using Singularity.Contracts.UltraStar;
using Singularity.Tools.ChartBench;
using Xunit;

namespace Singularity.Tests.Tools;

public class ChartComparisonTests
{
    private static UltraStarSong Song(int gapMs, params (int Start, int Length, int Tone)[] notes) => new()
    {
        Title = "t", Artist = "a", AudioFile = "a.mp3", Bpm = 300, GapMs = gapMs, // 50 ms per beat
        Voices = new[] { new UltraStarVoice(notes.Select(n => new UltraStarNote(NoteType.Regular, n.Start, n.Length, n.Tone, "x")).ToArray()) },
    };

    [Fact]
    public void IdenticalCharts_ScorePerfectly()
    {
        var song = Song(1000, (0, 4, 60), (6, 4, 62), (12, 8, 64));
        var c = ChartComparison.Compare(song, song);
        Assert.Equal((1.0, 1.0, 1.0, 1.0, 1.0), (c.Recall100, c.Precision100, c.Coverage, c.PitchClass, c.PitchWithinSemitone));
        Assert.Equal(0, c.MedianOnsetErrorMs);
    }

    [Fact]
    public void OctaveDifference_IsNotAPitchError()
    {
        var c = ChartComparison.Compare(Song(0, (0, 10, 72)), Song(0, (0, 10, 60)));
        Assert.Equal(1.0, c.PitchClass);
    }

    [Fact]
    public void ShiftedChart_MeasuresTheOffset()
    {
        // Generated notes start 80 ms late: inside 100 ms, outside 50 ms.
        var c = ChartComparison.Compare(Song(1000, (0, 10, 60), (20, 10, 62)), Song(1080, (0, 10, 60), (20, 10, 62)));
        Assert.Equal(1.0, c.Recall100);
        Assert.Equal(0.0, c.Recall50);
        Assert.Equal(80, c.MedianOnsetErrorMs);
        Assert.Equal(420.0 / 500.0, c.Coverage, 3);
    }

    [Fact]
    public void ExtraGeneratedNotes_LowerPrecisionOnly()
    {
        var c = ChartComparison.Compare(Song(0, (0, 10, 60)), Song(0, (0, 10, 60), (40, 10, 60)));
        Assert.Equal(1.0, c.Recall100);
        Assert.Equal(0.5, c.Precision100);
    }
}
