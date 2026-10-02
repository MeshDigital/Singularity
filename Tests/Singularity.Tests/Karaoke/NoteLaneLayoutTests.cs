using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Display;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class NoteLaneLayoutTests
{
    private static NoteLaneLayout Layout(params (int Start, int Length, int Tone)[] notes)
    {
        var voice = new UltraStarVoice(notes.Select(n => new UltraStarNote(NoteType.Regular, n.Start, n.Length, n.Tone, "x")).ToArray());
        var line = new LyricsTimeline(voice).At(0).Current!;
        return new NoteLaneLayout(line, voice.Notes);
    }

    [Fact]
    public void TimeSpansTheLine_PitchSpansTheRange()
    {
        var lane = Layout((0, 4, 60), (4, 4, 72), (8, 8, 66)).Layout().ToArray();

        Assert.Equal(0, lane[0].X);
        Assert.Equal(0.25, lane[0].Width);
        Assert.Equal(0.5, lane[2].X);
        Assert.True(lane[0].Y < lane[2].Y && lane[2].Y < lane[1].Y);
        Assert.Equal(0.5, lane[2].Y, 6); // 66 is the middle of 60..72
        Assert.InRange(lane[0].Y, 0.01, 0.1);  // margin keeps it off the edge
    }

    [Fact]
    public void FlatLine_IsCentredNotStretched()
    {
        var lane = Layout((0, 4, 64), (4, 4, 65)).Layout().ToArray();
        Assert.InRange(lane[0].Y, 0.35, 0.5);
        Assert.InRange(lane[1].Y, 0.5, 0.65);
    }

    [Fact]
    public void SingerPitch_IsFoldedIntoTheLinesOctave()
    {
        var lane = Layout((0, 4, 60), (4, 4, 67));
        Assert.Equal(lane.YFor(64), lane.SingerY(64 - 24), 6); // a bass two octaves down
        Assert.Equal(lane.YFor(64), lane.SingerY(64 + 12), 6);
    }
}
