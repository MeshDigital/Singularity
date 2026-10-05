using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Display;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class NoteLaneLayoutTests
{
    private static UltraStarNote Note(int start, int length, int tone) => new(NoteType.Regular, start, length, tone, "x");
    private static UltraStarNote Break(int at) => new(NoteType.LineBreak, at, 0, 0, "");

    private static NoteLaneLayout Layout(params (int Start, int Length, int Tone)[] notes)
    {
        var voice = new UltraStarVoice(notes.Select(n => Note(n.Start, n.Length, n.Tone)).ToArray());
        var line = new LyricsTimeline(voice).At(0).Current!;
        return new NoteLaneLayout(line, voice.Notes);
    }

    [Fact]
    public void TimeSpansTheLine_PitchIsCentredOnTheLine()
    {
        var lane = Layout((0, 4, 60), (4, 4, 72), (8, 8, 66)).Layout().ToArray();

        Assert.Equal(0, lane[0].X);
        Assert.Equal(0.25, lane[0].Width);
        Assert.Equal(0.5, lane[2].X);
        Assert.True(lane[0].Y < lane[2].Y && lane[2].Y < lane[1].Y);
        Assert.Equal(0.5, lane[2].Y, 6); // 66 is the middle of 60..72
        Assert.InRange(lane[0].Y, 0.05, 0.3); // the margin keeps it off the edge
    }

    [Fact]
    public void ASemitone_IsTheSameHeightOnEveryLine()
    {
        // A wide line (an octave) and a narrow one (a semitone): the scale doesn't change between them.
        var voice = new UltraStarVoice(new[] { Note(0, 4, 60), Note(4, 4, 72), Break(10), Note(12, 4, 64), Note(16, 4, 65) });
        var timeline = new LyricsTimeline(voice);
        var wide = new NoteLaneLayout(timeline.At(0).Current!, voice.Notes);
        var narrow = new NoteLaneLayout(timeline.At(13).Current!, voice.Notes);

        Assert.Equal(wide.Span, narrow.Span);
        Assert.Equal(wide.YFor(61) - wide.YFor(60), narrow.YFor(65) - narrow.YFor(64), 9);
        Assert.Equal(64.5, narrow.Centre);
    }

    [Fact]
    public void Span_FitsTheWidestLine_WithinLimits()
    {
        Assert.Equal(NoteLaneLayout.MinSpan, NoteLaneLayout.SpanFor(new[] { Note(0, 4, 60), Note(4, 4, 62) }));
        Assert.Equal(15, NoteLaneLayout.SpanFor(new[] { Note(0, 4, 60), Note(4, 4, 72), Break(9), Note(10, 4, 50) }));
        Assert.Equal(NoteLaneLayout.MaxSpan, NoteLaneLayout.SpanFor(new[] { Note(0, 4, 40), Note(4, 4, 80) }));
    }

    [Fact]
    public void Layout_FollowsTheGlidingCentre()
    {
        var lane = Layout((0, 4, 60), (4, 4, 64));
        Assert.True(lane.Layout(lane.Centre + 2).First().Y < lane.Layout().First().Y); // view moved up: the note sits lower
    }

    [Fact]
    public void SingerPitch_IsFoldedToTheNoteBeingSung()
    {
        var lane = Layout((0, 4, 60), (4, 4, 67));
        Assert.Equal(60, lane.FoldToTarget(60 - 24, 1), 6);   // a bass two octaves down, on the first note
        Assert.Equal(67, lane.FoldToTarget(67 + 12, 5), 6);   // an octave up, on the second
        Assert.Equal(66.5, lane.FoldToTarget(54.5, 5), 6);    // slightly flat stays slightly flat
        Assert.Equal(lane.YFor(67), lane.SingerY(55, 5), 6);
    }

    [Fact]
    public void Target_IsTheNoteUnderTheCursor_OrTheNextOne()
    {
        var lane = Layout((0, 4, 60), (10, 4, 67));
        Assert.Equal(60, lane.TargetAt(2)!.MidiTone);
        Assert.Equal(60, lane.TargetAt(6)!.MidiTone);  // in the gap, the next note is still far: stay on the last
        Assert.Equal(67, lane.TargetAt(9)!.MidiTone);  // within the lookahead
        Assert.Equal(67, lane.TargetAt(20)!.MidiTone); // after the line: the last note
    }
}

public class PitchSmootherTests
{
    [Fact]
    public void ASingleSpike_IsIgnored()
    {
        var s = new PitchSmoother();
        foreach (var m in new[] { 60.0, 60.0, 60.0 }) s.Add(m);
        var spiked = s.Add(72);
        Assert.InRange(spiked!.Value, 59.9, 60.1);
        Assert.InRange(s.Add(60)!.Value, 59.9, 60.1);
    }

    [Fact]
    public void Flutter_IsSteadied()
    {
        var s = new PitchSmoother();
        double? last = null;
        for (int i = 0; i < 40; i++) last = s.Add(60 + (i % 2 == 0 ? 0.3 : -0.3));
        Assert.InRange(last!.Value, 59.85, 60.15);
    }

    [Fact]
    public void ANewNote_IsTakenAtOnce_NotGlidedTo()
    {
        var s = new PitchSmoother();
        for (int i = 0; i < 5; i++) s.Add(60);
        s.Add(65);
        Assert.InRange(s.Add(65)!.Value, 64.9, 65.1);
    }

    [Fact]
    public void Silence_StartsAfresh()
    {
        var s = new PitchSmoother();
        for (int i = 0; i < 5; i++) s.Add(60);
        Assert.Null(s.Add(null));
        Assert.Equal(62, s.Add(62));
    }

    [Fact]
    public void Snap_PullsANearlyRightPitchHalfwayOntoTheNote()
    {
        Assert.Equal(60.1, PitchSmoother.Snap(60.2, 60), 9);
        Assert.Equal(60.5, PitchSmoother.Snap(60.5, 60), 9);
    }
}
