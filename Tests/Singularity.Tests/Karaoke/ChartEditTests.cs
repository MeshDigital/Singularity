using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Editing;
using Singularity.Karaoke.Scoring;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class ChartEditTests
{
    // 150 BPM: 100 ms a beat. Line 1: beats 0-8, line 2: 20-28, line 3: 40-48.
    private static UltraStarSong Song() => new()
    {
        Title = "t", Artist = "a", AudioFile = "a.mp3", Bpm = 150, GapMs = 1000,
        Voices = new[]
        {
            new UltraStarVoice(new[]
            {
                new UltraStarNote(NoteType.Regular, 0, 4, 60, "one"), new UltraStarNote(NoteType.Regular, 4, 4, 62, " two"),
                UltraStarNote.LineBreak(10),
                new UltraStarNote(NoteType.Regular, 20, 4, 64, "three"), new UltraStarNote(NoteType.Golden, 24, 4, 65, " four"),
                UltraStarNote.LineBreak(30),
                new UltraStarNote(NoteType.Regular, 40, 8, 67, "five"),
            }),
        },
    };

    [Fact]
    public void Lines_AreNumberedFromOne_WithoutTheBreaks()
    {
        var lines = ChartEdit.Lines(Song().Voices[0]);
        Assert.Equal(new[] { new ChartLine(1, 0, 2), new ChartLine(2, 3, 5), new ChartLine(3, 6, 7) }, lines);
    }

    [Fact]
    public void ShiftAll_MovesTheGap()
    {
        Assert.Equal(1250, ChartEdit.ShiftAll(Song(), 250).GapMs);
    }

    [Fact]
    public void ALineMoves_ButNotIntoItsNeighbours()
    {
        var (moved, by) = ChartEdit.ShiftLine(Song(), 0, 2, 5);
        Assert.Equal(5, by);
        Assert.Equal(25, moved.Voices[0].Notes[3].StartBeat);

        var (_, far) = ChartEdit.ShiftLine(Song(), 0, 2, 50); // line 3 starts at 40, line 2 ends at 28
        Assert.Equal(12, far);
        var (_, back) = ChartEdit.ShiftLine(Song(), 0, 2, -50); // line 1 ends at 8
        Assert.Equal(-12, back);
        var (_, first) = ChartEdit.ShiftLine(Song(), 0, 1, -5); // not before beat 0
        Assert.Equal(0, first);
        var (_, last) = ChartEdit.ShiftLine(Song(), 0, 3, -10); // the last line, earlier
        Assert.Equal(-10, last);
    }

    [Fact]
    public void LineBreaks_StayBetweenTheLinesTheySeparate()
    {
        var (moved, _) = ChartEdit.ShiftLine(Song(), 0, 2, -12); // line 2 now 8-16; its break before was at 10
        var notes = moved.Voices[0].Notes;
        Assert.InRange(notes[2].StartBeat, 8, 8);   // between line 1's end (8) and line 2's start (8)
        Assert.InRange(notes[5].StartBeat, 16, 40); // after line 2's end (16), before line 3 (40)
    }

    [Fact]
    public void TransposeLine_AndSetTone()
    {
        var up = ChartEdit.TransposeLine(Song(), 0, 2, 2);
        Assert.Equal(new[] { 60, 62, 0, 66, 67, 0, 67 }, up.Voices[0].Notes.Select(n => n.Type == NoteType.LineBreak ? 0 : n.MidiTone));

        var one = ChartEdit.SetTone(Song(), 0, 1, 59);
        Assert.Equal(59, one.Voices[0].Notes[1].MidiTone);
        Assert.Equal(NoteType.LineBreak, ChartEdit.SetTone(Song(), 0, 2, 70).Voices[0].Notes[2].Type); // a break has no pitch
    }

    [Fact]
    public void UseSingerPitch_MovesOnlyNotesTheSingerSingsSteadilyElsewhere()
    {
        var song = Song();
        // Line 2 (beats 20-28 -> 3000-3800 ms): the singer sings "three" at 66 (chart 64) steadily, "four" as charted.
        var readings = new List<(double, double)>();
        for (double ms = 3000; ms < 3400; ms += 10) readings.Add((ms, 66 + 0.1 * Math.Sin(ms)));
        for (double ms = 3400; ms < 3800; ms += 10) readings.Add((ms, 65));
        var singer = ReferencePitch.FromReadings(readings, 6000);

        var (fixedSong, changed) = ChartEdit.UseSingerPitch(song, 0, 2, singer);

        Assert.Equal(1, changed);
        Assert.Equal(66, fixedSong.Voices[0].Notes[3].MidiTone);
        Assert.Equal(65, fixedSong.Voices[0].Notes[4].MidiTone);
        Assert.Equal(0, ChartEdit.UseSingerPitch(song, 0, 1, singer).Changed); // line 1: not sung at all
    }

    [Fact]
    public void AnEditedChart_WritesAndReadsBack()
    {
        var (edited, _) = ChartEdit.ShiftLine(ChartEdit.TransposeLine(Song(), 0, 1, -1), 0, 3, 2);
        var again = UltraStarSerializer.Read(UltraStarSerializer.Write(edited));
        Assert.Equal(edited.Voices[0].Notes.Select(n => (n.Type, n.StartBeat, n.MidiTone)), again.Voices[0].Notes.Select(n => (n.Type, n.StartBeat, n.MidiTone)));
    }
}
