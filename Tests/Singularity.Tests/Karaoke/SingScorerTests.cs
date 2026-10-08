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
    private static SingScorer Sing(Func<double, double?> sing, Difficulty difficulty = Difficulty.Medium, UltraStarVoice? voice = null, bool lineBonus = true,
        double beatMs = 0)
    {
        var scorer = new SingScorer(voice ?? Voice, difficulty, lineBonus, beatMs);
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
    [InlineData(Difficulty.Easy, 1.75)]
    [InlineData(Difficulty.Medium, 1.0)]
    [InlineData(Difficulty.Hard, 0.65)]
    public void BeyondTheTolerance_EarnsNothing(Difficulty difficulty, double semitonesOff)
    {
        Assert.Equal(0, Sing(b => Perfect(b) + semitonesOff, difficulty).Score.Total);
        Assert.Equal(0, Sing(b => Perfect(b) - semitonesOff - 0.1, difficulty).Score.Total);
    }

    [Theory]
    [InlineData(Difficulty.Easy)]
    [InlineData(Difficulty.Medium)]
    [InlineData(Difficulty.Hard)]
    public void Within20Cents_IsPerfectOnEveryDifficulty(Difficulty difficulty)
    {
        Assert.Equal(10_000, Sing(b => Perfect(b) + 0.15, difficulty).Score.Total);
        Assert.Equal(10_000, Sing(b => Perfect(b) - 0.2, difficulty).Score.Total);
    }

    [Fact]
    public void CloserIsBetter()
    {
        // Half a semitone sharp on Medium: 1 - (0.3 / 0.8)^2 = 0.859 of every beat and every line bonus.
        Assert.Equal(8590, Sing(b => Perfect(b) + 0.5).Score.Total);
        int quarter = Sing(b => Perfect(b) + 0.35).Score.Total, half = Sing(b => Perfect(b) + 0.5).Score.Total, most = Sing(b => Perfect(b) + 0.8).Score.Total;
        Assert.True(quarter > half && half > most && most > 0);
        Assert.True(Sing(b => Perfect(b) + 0.5, Difficulty.Easy).Score.Total > half);
        Assert.True(Sing(b => Perfect(b) + 0.5, Difficulty.Hard).Score.Total < half);
    }

    [Fact]
    public void Credit_FallsOffQuadratically()
    {
        Assert.Equal(1, SingScorer.CreditFor(0.2, Difficulty.Medium));
        Assert.Equal(0.75, SingScorer.CreditFor(-0.6, Difficulty.Medium), 9); // halfway between 0.2 and 1.0
        Assert.Equal(0, SingScorer.CreditFor(1.0, Difficulty.Medium));
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
    public void GapsUpToHalfABeat_AreForgiven()
    {
        // Voice in 2 of 4 samples per beat (consonants, breaths): full credit. In 1 of 4: half.
        Assert.Equal(10_000, Sing(b => b % 1 < 0.5 ? Perfect(b) : null).Score.Total);
        Assert.Equal(5_000, Sing(b => b % 1 < 0.25 ? Perfect(b) : null).Score.Total);
    }

    [Fact]
    public void AScoopOntoTheNote_IsForgiven()
    {
        // Every note starts two semitones flat for half a beat, then lands. 100 ms beats: an 80 ms grace.
        double? Scoop(double b) => Perfect(b) is { } m && (b % 1 < 0.5 && (b is < 0.5 or >= 4 and < 4.5 or >= 10 and < 10.5)) ? m - 2 : Perfect(b);
        Assert.Equal(10_000, Sing(Scoop, beatMs: 100).Score.Total);
        Assert.True(Sing(Scoop).Score.Total < 10_000); // without the grace the scoop costs points
    }

    [Fact]
    public void ABeatThatIsAllAttack_IsJudgedLikeTheNextBeat()
    {
        // 50 ms beats: the grace covers the first 1.6 beats, so beat 0 is all scoop and takes beat 1's verdict.
        double? Scoop(double b) => Perfect(b) is { } m && (b is < 1 or >= 4 and < 5 or >= 10 and < 11) ? m - 2 : Perfect(b);
        Assert.Equal(10_000, Sing(Scoop, beatMs: 50).Score.Total);
    }

    [Fact]
    public void TheGrace_DoesNotRewardSilence()
    {
        Assert.Equal(0, Sing(_ => null, beatMs: 50).Score.Total);
    }

    [Fact]
    public void Judgements_ReportHowFarOff()
    {
        var judged = new List<BeatJudgement>();
        var scorer = new SingScorer(Voice);
        scorer.BeatJudged += judged.Add;
        for (double beat = 0; beat < 16; beat += 0.25) scorer.AddSample(beat, Perfect(beat) + 12.3); // an octave and 30 cents up
        scorer.Finish();

        Assert.All(judged, j => Assert.Equal(0.3, j.Offset, 6));
        Assert.All(judged, j => Assert.True(j.Hit && j.Credit < 1));
    }

    [Fact]
    public void OnTime_KeepsTheWholeLineBonus()
    {
        var s = Sing(Perfect, beatMs: 100);
        Assert.Equal(10_000, s.Score.Total);
        Assert.All(s.CompletedLines, l => Assert.Equal(1, l.Timing));
    }

    [Fact]
    public void StartingNotesLate_CostsALittleOfTheLineBonus()
    {
        // Every note started 2 beats (200 ms) late, then sung right: the notes' own points are mostly kept.
        // Notes start at beats 0, 4 and 10; the singer comes in 2 beats into each.
        double? Late(double b) => (b < 8 ? b % 4 : b - 10) < 2 ? null : Perfect(b);
        var onTime = Sing(Perfect, beatMs: 100);
        var late = Sing(Late, beatMs: 100);

        Assert.All(late.CompletedLines, l => Assert.InRange(l.Timing, 0.6, 0.75)); // 200 ms: 1 - (200-120)/280
        // Each line's bonus: its share for how well it was sung, less up to a quarter for coming in late.
        Assert.All(late.CompletedLines, l =>
            Assert.Equal(500 * l.Perfection * (1 - SingScorer.TimingShareOfBonus * (1 - l.Timing)), l.Bonus, 6));
        Assert.True(late.Score.LineBonus < onTime.Score.LineBonus);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(120, 1)]
    [InlineData(260, 0.5)]
    [InlineData(400, 0)]
    [InlineData(900, 0)]
    public void OnTime_FallsOffBetween120And400Ms(double lateMs, double credit) =>
        Assert.Equal(credit, SingScorer.OnTime(lateMs), 6);

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
        scorer.BeatJudged += j => judged.Add((j.Beat, j.Hit));
        for (double beat = 0; beat < 16; beat += 0.25) scorer.AddSample(beat, beat < 2 ? 60 : null);
        scorer.Finish();

        Assert.Equal(Enumerable.Range(0, 8).Concat(Enumerable.Range(10, 4)), judged.Select(j => j.Beat));
        Assert.Equal(new[] { 0, 1 }, judged.Where(j => j.Hit).Select(j => j.Beat));
    }
}

public class ScoreTitlesTests
{
    [Theory]
    [InlineData(0, "Tone Deaf")]
    [InlineData(1999, "Tone Deaf")]
    [InlineData(2000, "Amateur")]
    [InlineData(5990, "Wannabe")]
    [InlineData(7490, "Rising Star")]
    [InlineData(8990, "Lead Singer")]
    [InlineData(10000, "Karaoke God")]
    public void Tiers(int score, string title) => Assert.Equal(title, ScoreTitles.For(score));
}

public class DisplayedScoreTests
{
    [Theory]
    [InlineData(12.9, 0, 11.9)]
    [InlineData(4495.5, 4490.2, 1000)]
    [InlineData(0, 0, 0)]
    [InlineData(9000, 0, 1000)]
    [InlineData(3333.3, 3333.3, 333.3)]
    public void PartsAlwaysAddUpToTheTotal(double notes, double golden, double bonus)
    {
        var score = new ScoreBreakdown(notes, golden, bonus);
        var shown = DisplayedScore.From(score);
        Assert.Equal(score.Total, shown.Notes + shown.Golden + shown.LineBonus);
        Assert.Equal(score.Total, shown.Total);
        Assert.All(new[] { shown.Notes, shown.Golden, shown.LineBonus }, p => Assert.Equal(0, p % 10));
    }
}
