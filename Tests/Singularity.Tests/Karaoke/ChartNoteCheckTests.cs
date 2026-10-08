using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Scoring;
using Singularity.Karaoke.Sync;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class ChartNoteCheckTests
{
    // 150 BPM: 100 ms a beat, no gap. Lines of four 4-beat notes with a 2-beat pause between lines.
    private static UltraStarSong Chart(int lines, Func<int, int> toneOf)
    {
        var notes = new List<UltraStarNote>();
        int beat = 0, k = 0;
        for (int l = 0; l < lines; l++)
        {
            for (int i = 0; i < 4; i++, k++)
            {
                notes.Add(new UltraStarNote(NoteType.Regular, beat, 4, toneOf(k), "la"));
                beat += 4;
            }
            notes.Add(UltraStarNote.LineBreak(beat));
            beat += 2;
        }
        return new UltraStarSong { Title = "t", Artist = "a", AudioFile = "a.mp3", Bpm = 150, GapMs = 0, Voices = new[] { new UltraStarVoice(notes.ToArray()) } };
    }

    /// <summary>A singer who sings <paramref name="sung"/>(note index) over each note of <paramref name="chart"/>, with a little vibrato.</summary>
    private static ReferencePitch Singer(UltraStarSong chart, Func<int, double> sung)
    {
        var readings = new List<(double, double)>();
        int k = 0;
        foreach (var n in chart.Voices[0].Notes)
        {
            if (n.Type == NoteType.LineBreak) continue;
            double pitch = sung(k++);
            for (double ms = chart.BeatToMs(n.StartBeat); ms < chart.BeatToMs(n.StartBeat + n.DurationBeats); ms += 10)
                readings.Add((ms, pitch + 0.15 * Math.Sin(ms / 30)));
        }
        return ReferencePitch.FromReadings(readings, chart.BeatToMs(1000));
    }

    private static int Tone(int k) => 60 + k % 5;

    [Fact]
    public void AChartTheSingerSings_Agrees()
    {
        var chart = Chart(12, Tone);
        var result = ChartNoteCheck.Check(chart, Singer(chart, k => Tone(k) - 12)); // an octave down is still the note

        Assert.Equal(48, result.Judged);
        Assert.Equal(1.0, result.Agreement);
        Assert.Empty(result.LinesToCheck);
        Assert.Empty(result.Corrections);
        Assert.False(result.Mismatch);
    }

    [Fact]
    public void ANoteSungSteadilyElsewhere_IsCorrected()
    {
        var chart = Chart(12, Tone);
        var result = ChartNoteCheck.Check(chart, Singer(chart, k => k == 5 ? Tone(5) + 2 : Tone(k)));

        var fix = Assert.Single(result.Corrections);
        Assert.Equal(Tone(5) + 2, fix.Tone);
        var fixedChart = ChartNoteCheck.Apply(chart, result.Corrections);
        Assert.Equal(1.0, ChartNoteCheck.Check(fixedChart, Singer(chart, k => k == 5 ? Tone(5) + 2 : Tone(k))).Agreement);
    }

    [Fact]
    public void ALineSungElsewhere_IsToCheck()
    {
        var chart = Chart(12, Tone);
        var result = ChartNoteCheck.Check(chart, Singer(chart, k => k / 4 == 3 ? Tone(k) + 3 : Tone(k))); // line 4

        Assert.Equal(new[] { 4 }, result.LinesToCheck);
    }

    [Fact]
    public void AnotherSongsChart_IsAMismatch()
    {
        var chart = Chart(12, Tone);
        var result = ChartNoteCheck.Check(chart, Singer(chart, k => 60 + (k * 7 + 3) % 12));

        Assert.True(result.Mismatch, $"{result.Agreement:P0}");
    }

    [Fact]
    public void NotesTheSingerHardlySings_AreNotJudged()
    {
        var chart = Chart(2, Tone);
        Assert.Equal(0, ChartNoteCheck.Check(chart, ReferencePitch.FromReadings(Array.Empty<(double, double)>(), 5000)).Judged);
    }
}
