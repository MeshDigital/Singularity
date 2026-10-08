using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Scoring;
using Singularity.Karaoke.Sync;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class ChartMelodyTests
{
    // 150 BPM, 100 ms a beat. A C-major melody of 4-beat notes.
    private static readonly int[] Melody = { 60, 62, 64, 65, 67, 69, 71, 72, 67, 64, 62, 60 };

    private static UltraStarSong Chart(Func<int, int> toneOf) => new()
    {
        Title = "t", Artist = "a", AudioFile = "a.mp3", Bpm = 150, GapMs = 0,
        Voices = new[] { new UltraStarVoice(Melody.Select((_, i) => new UltraStarNote(NoteType.Regular, i * 4, 4, toneOf(i), "la")).ToArray()) },
    };

    /// <summary>A singer: <paramref name="sung"/>(note index) over each note, with a slide in at the start.</summary>
    private static ReferencePitch Singer(Func<int, double> sung)
    {
        var readings = new List<(double, double)>();
        for (int i = 0; i < Melody.Length; i++)
            for (double ms = i * 400; ms < i * 400 + 400; ms += 10)
                readings.Add((ms, ms - i * 400 < 60 ? sung(i) - 2 : sung(i))); // scoops up for the first 60 ms
        return ReferencePitch.FromReadings(readings, Melody.Length * 400 + 1000);
    }

    [Fact]
    public void NotesTakeWhatTheSingerSings_NotTheSlideIn()
    {
        var chart = Chart(i => i == 3 ? 66 : Melody[i]); // the worker misheard note 3
        var (fitted, changed) = ChartMelody.FitNotes(chart, Singer(i => Melody[i]));

        Assert.Equal(1, changed);
        Assert.Equal(Melody, fitted.Voices[0].Notes.Select(n => n.MidiTone));
    }

    [Fact]
    public void ANoteBetweenTwoSemitones_TakesTheOneInTheKey()
    {
        // Note 2 sung at 63.6: nearer 64 (E, in C major) anyway; note 6 sung at 70.6: nearer 71 (B, in key).
        // Note 4 sung at 67.6: nearer 68 (G#, not in C major), and far enough from it: G (67) is taken.
        var (fitted, _) = ChartMelody.FitNotes(Chart(i => Melody[i]), Singer(i => i == 4 ? 67.6 : Melody[i]));
        Assert.Equal(67, fitted.Voices[0].Notes[4].MidiTone);

        // Without the key it stays where the singer is nearest.
        var (plain, _) = ChartMelody.FitNotes(Chart(i => Melody[i]), Singer(i => i == 4 ? 67.6 : Melody[i]), snapBeyond: null);
        Assert.Equal(68, plain.Voices[0].Notes[4].MidiTone);
    }

    [Fact]
    public void AClearOutOfKeyNote_IsKept()
    {
        // Sung right on G# (68.0): a deliberate chromatic note, not an in-between reading.
        var (fitted, _) = ChartMelody.FitNotes(Chart(i => Melody[i]), Singer(i => i == 4 ? 68.0 : Melody[i]));
        Assert.Equal(68, fitted.Voices[0].Notes[4].MidiTone);
    }

    [Fact]
    public void TheOctaveOfTheChartIsKept()
    {
        var (fitted, changed) = ChartMelody.FitNotes(Chart(i => Melody[i]), Singer(i => Melody[i] - 12)); // a bass, an octave down
        Assert.Equal(0, changed);
        Assert.Equal(Melody, fitted.Voices[0].Notes.Select(n => n.MidiTone));
    }

    [Fact]
    public void TheKey_IsFoundFromTheNotes()
    {
        var histogram = new double[12];
        foreach (var t in Melody) histogram[t % 12] += 4;
        Assert.Equal(new HashSet<int> { 0, 2, 4, 5, 7, 9, 11 }, ChartMelody.KeyScale(histogram));
    }
}
