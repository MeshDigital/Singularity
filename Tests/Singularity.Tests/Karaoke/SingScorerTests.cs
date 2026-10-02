using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Scoring;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class SingScorerTests
{
    // Line 1: two regular notes (4 beats each) at C4 and D4; line 2: one golden 4-beat note at E4.
    private static readonly UltraStarVoice Voice = new(new[]
    {
        new UltraStarNote(NoteType.Regular, 0, 4, 60, "one"),
        new UltraStarNote(NoteType.Regular, 4, 4, 62, " two"),
        UltraStarNote.LineBreak(8),
        new UltraStarNote(NoteType.Golden, 10, 4, 64, "three"),
    });

    /// <summary>Sings along: <paramref name="sing"/> maps a beat to the sung MIDI pitch (null = silent), 4 samples per beat.</summary>
    private static SingScorer Sing(Func<double, double?> sing, Difficulty difficulty = Difficulty.Medium, UltraStarVoice? voice = null, bool lineBonus = true)
    {
        var scorer = new SingScorer(voice ?? Voice, difficulty, lineBonus);
        for (double beat = 0; beat < 16; beat += 0.25) scorer.AddSample(beat, sing(beat));
        scorer.Finish();
        return scorer;
    }

    private static double? Perfect(double beat) => beat switch { < 4 => 60, < 8 => 62, >= 10 and < 14 => 64, _ => null };

    [Fact]
    public void PerfectSinging_ScoresTenThousand()
    {
        var s = Sing(Perfect);
        Assert.Equal(10_000, s.Score.Total);
        Assert.All(s.CompletedLines, l => Assert.Equal(LineRating.Perfect, l.Rating));
    }

    [Fact]
    public void Silence_ScoresZero()
    {
        var s = Sing(_ => null);
        Assert.Equal(0, s.Score.Total);
        Assert.All(s.CompletedLines, l => Assert.Equal(LineRating.Awful, l.Rating));
    }

    [Fact]
    public void GoldenBeats_WeighDouble()
    {
        // Only the golden note: weight 8 of 16 -> half the 9000 note points, plus line 2's full bonus.
        var s = Sing(b => b >= 10 ? 64 : null);
        Assert.Equal(4500, s.Score.Golden, 6);
        Assert.Equal(0, s.Score.Notes);
        Assert.Equal(500, s.Score.LineBonus, 6);
    }

    [Fact]
    public void AnyOctave_Counts()
    {
        var s = Sing(b => Perfect(b) - 24); // two octaves down
        Assert.Equal(10_000, s.Score.Total);
    }

    [Theory]
    [InlineData(Difficulty.Easy, 2, true)]
    [InlineData(Difficulty.Easy, 3, false)]
    [InlineData(Difficulty.Medium, 1, true)]
    [InlineData(Difficulty.Medium, 2, false)]
    [InlineData(Difficulty.Hard, 0.4, true)]
    [InlineData(Difficulty.Hard, 1, false)]
    public void Tolerance_FollowsDifficulty(Difficulty difficulty, double semitonesOff, bool hits)
    {
        var s = Sing(b => Perfect(b) + semitonesOff, difficulty);
        Assert.Equal(hits ? 10_000 : 0, s.Score.Total);
    }

    [Fact]
    public void RapNotes_NeedOnlyVoice()
    {
        var rap = new UltraStarVoice(new[] { new UltraStarNote(NoteType.Rap, 0, 8, 60, "yo") });
        Assert.Equal(10_000, Sing(_ => 71.3, voice: rap).Score.Total);
    }

    [Fact]
    public void FreestyleNotes_AreNotScored()
    {
        var voice = new UltraStarVoice(new[]
        {
            new UltraStarNote(NoteType.Freestyle, 0, 8, 60, "la"),
            new UltraStarNote(NoteType.Regular, 8, 4, 60, "do"),
        });
        Assert.Equal(10_000, Sing(b => b >= 8 && b < 12 ? 60 : null, voice: voice).Score.Total);
    }

    [Fact]
    public void HalfALine_IsRatedAndBonusedProportionally()
    {
        var results = new List<LineResult>();
        var scorer = new SingScorer(Voice);
        scorer.LineCompleted += results.Add;
        for (double beat = 0; beat < 16; beat += 0.25) scorer.AddSample(beat, beat < 4 ? 60 : null); // only "one"
        scorer.Finish();

        Assert.Equal(new[] { 0, 1 }, results.Select(r => r.LineIndex));
        Assert.Equal(0.5, results[0].Perfection);
        Assert.Equal(LineRating.Good, results[0].Rating);
        Assert.Equal(250, results[0].Bonus, 6);
        Assert.Equal(2250 + 250, scorer.Score.Total);
    }

    [Fact]
    public void ABeatNeedsHalfItsSamplesRight()
    {
        // 2 of 4 samples per beat right: still a hit. 1 of 4: a miss.
        Assert.Equal(10_000, Sing(b => b % 1 < 0.5 ? Perfect(b) : null).Score.Total);
        Assert.Equal(0, Sing(b => b % 1 < 0.25 ? Perfect(b) : null).Score.Total);
    }

    [Fact]
    public void WithoutLineBonus_NotesAreWorthTenThousand()
    {
        var s = Sing(Perfect, lineBonus: false);
        Assert.Equal(10_000, s.Score.Total);
        Assert.Equal(0, s.Score.LineBonus);
    }

    [Fact]
    public void EveryBeatOfAScoredNote_IsReportedOnce()
    {
        var judged = new List<(int Beat, bool Hit)>();
        var scorer = new SingScorer(Voice);
        scorer.BeatJudged += (_, beat, hit) => judged.Add((beat, hit));
        for (double beat = 0; beat < 16; beat += 0.25) scorer.AddSample(beat, beat < 2 ? 60 : null);
        scorer.Finish();

        Assert.Equal(Enumerable.Range(0, 8).Concat(Enumerable.Range(10, 4)), judged.Select(j => j.Beat));
        Assert.Equal(new[] { 0, 1 }, judged.Where(j => j.Hit).Select(j => j.Beat));
    }
}
