using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Display;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class LyricsTimelineTests
{
    private static readonly UltraStarVoice Voice = new(new[]
    {
        new UltraStarNote(NoteType.Regular, 10, 4, 60, "Is"),
        new UltraStarNote(NoteType.Regular, 14, 4, 60, " this"),
        new UltraStarNote(NoteType.Golden, 18, 4, 62, " real"),
        new UltraStarNote(NoteType.Regular, 22, 4, 60, "~"),
        UltraStarNote.LineBreak(26),
        new UltraStarNote(NoteType.Regular, 40, 4, 64, "Fan"),
        new UltraStarNote(NoteType.Regular, 44, 4, 62, "ta"),
        new UltraStarNote(NoteType.Regular, 48, 4, 60, "sy"),
    });

    private readonly LyricsTimeline _timeline = new(Voice);

    [Fact]
    public void BeforeTheFirstLine_ShowsItWithACountdown()
    {
        var frame = _timeline.At(4);
        Assert.Equal("Is this real", frame.Current!.Text);
        Assert.Equal("Fantasy", frame.Next!.Text);
        Assert.Equal(6, frame.BeatsUntilStart);
        Assert.All(frame.Current.Syllables, s => Assert.Equal(0, s.Progress));
    }

    [Fact]
    public void DuringALine_SyllablesWipeInOrder()
    {
        var frame = _timeline.At(16);
        var progress = frame.Current!.Syllables.Select(s => s.Progress).ToArray();
        Assert.Equal(new[] { 1.0, 0.5, 0.0 }, progress);
        Assert.Equal(0, frame.BeatsUntilStart);
    }

    [Fact]
    public void TildeNotes_ExtendTheHeldSyllable()
    {
        var real = _timeline.At(22).Current!.Syllables[2];
        Assert.Equal(" real", real.Text);
        Assert.Equal((18, 26), (real.StartBeat, real.EndBeat));
        Assert.Equal(0.5, real.Progress);
        Assert.Equal(NoteType.Golden, real.Type);
    }

    [Fact]
    public void BetweenLines_TheNextLineIsCurrentWithItsCountdown()
    {
        var frame = _timeline.At(30);
        Assert.Equal("Fantasy", frame.Current!.Text);
        Assert.Null(frame.Next);
        Assert.Equal(10, frame.BeatsUntilStart);
    }

    [Fact]
    public void AfterTheLastLine_ShowsNothing()
    {
        Assert.Null(_timeline.At(60).Current);
        Assert.Equal(2, _timeline.LineCount);
    }
}
