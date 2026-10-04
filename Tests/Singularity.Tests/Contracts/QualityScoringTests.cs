using Singularity.Contracts.Quality;
using Singularity.Contracts.Song;
using Xunit;

namespace Singularity.Tests.Contracts;

public class QualityScoringTests
{
    [Fact]
    public void PerfectMetrics_ScoreOne_APlus()
    {
        var q = QualityScoring.Assess(new QualityMetrics(1, 1, 1, 1, 1));
        Assert.Equal(1.0, q.OverallScore);
        Assert.Equal(QualityTier.APlus, q.Tier);
    }

    [Fact]
    public void NoVideo_CapsAt085_APlusOnlyWithAPerfectEverythingElse()
    {
        var q = QualityScoring.Assess(new QualityMetrics(1, 1, 1, 0, 1));
        Assert.Equal(0.85, q.OverallScore);
        Assert.Equal(QualityTier.APlus, q.Tier);
        // A duration-checked download (no fingerprint) without video can't reach A+.
        Assert.Equal(QualityTier.A, QualityScoring.Assess(new QualityMetrics(0.6, 1, 1, 0, 1)).Tier);
    }

    [Theory]
    [InlineData(0.10, 0.5)]
    [InlineData(0.18, 0.832)]
    [InlineData(0.0, 0.119)]
    [InlineData(0.31, 0.985)]
    public void LyricConfidence_IsNormalisedOnTheFittedCurve(double raw, double expected) =>
        Assert.Equal(expected, QualityScoring.NormalizeConfidence(raw, QualityScoring.LyricMidpoint, QualityScoring.LyricSteepness), 3);

    [Fact]
    public void SubScores_NormaliseTheRawScores()
    {
        // Median word confidence 0.18 (a typical AI chart) is a good lyric sub-score, not a failing one.
        Assert.InRange(QualityScoring.LyricSubScore(new[] { 0.1, 0.18, 0.3 }), 0.8, 0.86);
        // 7 of 20 voiced syllables confident: the pitch midpoint.
        Assert.Equal(0.5, QualityScoring.PitchSubScore(Enumerable.Repeat(0.9, 7).Concat(Enumerable.Repeat(0.1, 13))), 6);
    }

    [Theory]
    [InlineData(0.80, QualityTier.APlus)]
    [InlineData(0.7999, QualityTier.A)]
    [InlineData(0.68, QualityTier.A)]
    [InlineData(0.50, QualityTier.B)]
    [InlineData(0.4999, QualityTier.ReviewRequired)]
    [InlineData(0.0, QualityTier.ReviewRequired)]
    public void TierBoundaries(double score, QualityTier expected) =>
        Assert.Equal(expected, QualityScoring.TierFor(score));

    [Fact]
    public void ScoreIsRoundedBeforeTiering()
    {
        // 0.25*1 + 0.25*0.8 + 0.2*0.75 + 0.15*1 + 0.15*(2/3) = 0.85 exactly → rounding keeps tier stable.
        var q = QualityScoring.Assess(new QualityMetrics(1, 0.8, 0.75, 1, 2.0 / 3));
        Assert.Equal(0.85, q.OverallScore);
        Assert.Equal(QualityScoring.TierFor(q.OverallScore), q.Tier);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void OutOfRangeMetric_Throws(double bad) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityScoring.Assess(new QualityMetrics(1, bad, 1, 1, 1)));

    [Fact]
    public void SubScores()
    {
        Assert.Equal(1.0, QualityScoring.AudioMatchScore(AudioMatchKind.FingerprintMatch));
        Assert.Equal(0.6, QualityScoring.AudioMatchScore(AudioMatchKind.DurationOnly));
        Assert.Equal(0.0, QualityScoring.AudioMatchScore(AudioMatchKind.Mismatch));

        Assert.True(QualityScoring.IsDurationMatch(200_000, 202_000));
        Assert.False(QualityScoring.IsDurationMatch(200_000, 202_001));

        Assert.Equal(0.5, QualityScoring.PitchScore(new[] { 0.9, 0.75, 0.8, 0.1 }));
        Assert.Equal(0.0, QualityScoring.PitchScore(Array.Empty<double>()));

        Assert.Equal(2.0 / 3, QualityScoring.MetadataScore(true, false, true), 6);
        Assert.Equal(0.7, QualityScoring.LyricScore(new[] { 0.9, 0.5, 0.7 }));
        Assert.Equal(0.6, QualityScoring.LyricScore(new[] { 0.9, 0.5, 0.7, 0.1 }), 6);
        Assert.Equal(0.0, QualityScoring.LyricScore(Array.Empty<double>()));
    }
}

public class VideoGapConsensusTests
{
    [Fact]
    public void AgreeingWindows_ReturnMedian() =>
        Assert.Equal(new VideoGapResult(true, -340), VideoGapConsensus.Evaluate(new int?[] { -345, -340, -332 }));

    [Fact]
    public void AdjacentDifferenceAtTolerance_IsInvalid() =>
        Assert.False(VideoGapConsensus.Evaluate(new int?[] { 100, 115, 115 }).IsValid);

    [Fact]
    public void JustUnderTolerance_IsValid() =>
        Assert.True(VideoGapConsensus.Evaluate(new int?[] { 100, 114, 120 }).IsValid);

    [Fact]
    public void ExtendedIntro_StructureMismatch()
    {
        var r = VideoGapConsensus.Evaluate(new int?[] { 1200, 9200, 9205 });
        Assert.False(r.IsValid);
        Assert.Equal(0, r.VideoGapMs);
        Assert.Equal(0.0, r.Score);
    }

    [Fact]
    public void MissingPeakOrTooFewWindows_IsInvalid()
    {
        Assert.False(VideoGapConsensus.Evaluate(new int?[] { 10, null, 12 }).IsValid);
        Assert.False(VideoGapConsensus.Evaluate(new int?[] { 10, 12 }).IsValid);
    }

    [Fact]
    public void EvenWindowCount_AveragesMiddlePair() =>
        Assert.Equal(15, VideoGapConsensus.Evaluate(new int?[] { 10, 14, 16, 20 }).VideoGapMs);

    private static (double, int?)[] At(params int?[] offsets) =>
        offsets.Select((o, i) => (i * 60_000.0, o)).ToArray(); // one window per minute

    [Fact]
    public void Drift_SteadySlope_IsValidWithMidValue()
    {
        var r = VideoGapConsensus.EvaluateWithDrift(At(-2400, -2340, -2280, -2220, -2160));
        Assert.True(r.IsValid);
        Assert.Equal(-2280, r.VideoGapMs);
        Assert.Equal(60, r.DriftMsPerMinute);
    }

    [Fact]
    public void Drift_MissingWindowsAreSkipped_ButThreeAreNeeded()
    {
        var r = VideoGapConsensus.EvaluateWithDrift(At(6451, 6449, null, 6450, null));
        Assert.True(r.IsValid);
        Assert.Equal(6450, r.VideoGapMs);
        Assert.False(VideoGapConsensus.EvaluateWithDrift(At(6451, null, null, 6450, null)).IsValid);
    }

    [Fact]
    public void Drift_ArrangementJump_IsInvalid() =>
        Assert.False(VideoGapConsensus.EvaluateWithDrift(At(1200, 1200, 9200, 9200, 9200)).IsValid);

    [Fact]
    public void Drift_OneStrayWindow_IsForgivenWhenFourRemain()
    {
        Assert.Equal(410, VideoGapConsensus.EvaluateWithDrift(At(410, 410, 410, 410, -19990)).VideoGapMs);
        Assert.Equal(525, VideoGapConsensus.EvaluateWithDrift(At(528, 528, -12150, 522, 522)).VideoGapMs);
        Assert.False(VideoGapConsensus.EvaluateWithDrift(At(410, 410, 410, -19990, null)).IsValid);
    }

    [Fact]
    public void Drift_TooSteep_IsInvalid() =>
        Assert.False(VideoGapConsensus.EvaluateWithDrift(At(0, 150, 300, 450, 600)).IsValid);
}
