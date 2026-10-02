using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Library;
using Singularity.Tests.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace Singularity.Tests.Karaoke;

public class MedleyFinderTests(ITestOutputHelper output)
{
    /// <summary>A song whose lines are given as texts; each line lasts 16 beats (4 s at 60 BPM grid... 240 → 62.5 ms/beat).</summary>
    private static UltraStarSong Song(params string[] lineTexts)
    {
        var notes = new List<UltraStarNote>();
        int beat = 0;
        foreach (var text in lineTexts)
        {
            if (notes.Count > 0) notes.Add(UltraStarNote.LineBreak(beat));
            foreach (var word in text.Split(' '))
            {
                notes.Add(new UltraStarNote(NoteType.Regular, beat, 6, 60, " " + word));
                beat += 8;
            }
            beat += 8;
        }
        // 60 BPM grid: 250 ms per beat.
        return new UltraStarSong { Title = "t", Artist = "a", AudioFile = "a.mp3", Bpm = 60, Voices = new[] { new UltraStarVoice(notes) } };
    }

    [Fact]
    public void ChartTags_Win()
    {
        var song = Song("a b", "c d", "a b", "c d") with { MedleyStartBeat = 3, MedleyEndBeat = 40 };
        Assert.Equal(new MedleyRange(3, 40), MedleyFinder.Find(song));
    }

    [Fact]
    public void FindsTheFirstChorus()
    {
        var song = Song(
            "verse one goes here", "and then some more",
            "this is the chorus", "sing it loud tonight",
            "verse two is different", "with other words",
            "This is the chorus!", "Sing it loud, tonight");
        var range = MedleyFinder.Find(song)!.Value;

        // Each line is 4 words x 8 beats + 8 = 40 beats = 10 s. The chorus (lines 2-3) starts at beat 80
        // but lasts under MinSeconds, so the section takes in line 4 too; its last note ends at 160 + 24 + 6.
        Assert.Equal(80, range.StartBeat);
        Assert.Equal(190, range.EndBeat);
    }

    [Fact]
    public void NoRepeats_NoMedley() =>
        Assert.Null(MedleyFinder.Find(Song("one two three four", "five six seven eight", "nine ten eleven twelve", "thirteen fourteen fifteen sixteen")));

    [Fact]
    public void TooShortRepeat_IsIgnored()
    {
        var song = Song("la", "la", "verse words here now", "more verse words now", "la", "la") with { Bpm = 600 }; // 25 ms per beat
        Assert.Null(MedleyFinder.Find(song));
    }

    [Fact]
    public void Normalize_IgnoresCasePunctuationAndAccents() =>
        Assert.Equal(MedleyFinder.Normalize(" Ça, va?"), MedleyFinder.Normalize("ca VA"));

    [RequiresSongCollectionFact]
    public void AgreesWithHumanMedleyTags()
    {
        // The charts that mark a medley are ground truth: hide the tags and see what we find.
        int tagged = 0, found = 0, overlapping = 0;
        var ious = new List<double>();
        foreach (var txt in Directory.EnumerateFiles(UltraStarCorpusTests.SongsDir!, "*.txt", SearchOption.AllDirectories))
        {
            var song = UltraStarSerializer.ReadFile(txt);
            if (song.MedleyStartBeat is not { } s || song.MedleyEndBeat is not { } e || song.IsDuet) continue;
            tagged++;
            if (MedleyFinder.Find(song with { MedleyStartBeat = null, MedleyEndBeat = null }) is not { } r) continue;
            found++;
            double inter = Math.Max(0, Math.Min(e, r.EndBeat) - Math.Max(s, r.StartBeat));
            double union = Math.Max(e, r.EndBeat) - Math.Min(s, r.StartBeat);
            double iou = union > 0 ? inter / union : 0;
            ious.Add(iou);
            if (iou >= 0.5) overlapping++;
        }
        ious.Sort();
        output.WriteLine($"{tagged} tagged charts: found a medley for {found}, IoU >= 0.5 for {overlapping}, median IoU {(ious.Count > 0 ? ious[ious.Count / 2] : 0):0.00}");
        Assert.True(tagged > 0);
        Assert.True(found >= tagged * 0.9, $"found {found}/{tagged}");
        Assert.True(overlapping >= tagged / 3, $"agreed with {overlapping}/{tagged}"); // 40/108 when written
    }
}
