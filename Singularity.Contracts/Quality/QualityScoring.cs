using Singularity.Contracts.Song;

namespace Singularity.Contracts.Quality;

/// <summary>How the downloaded master audio was verified against the requested recording.</summary>
public enum AudioMatchKind
{
    /// <summary>Duration outside tolerance, or the fingerprint matched a different recording.</summary>
    Mismatch,
    /// <summary>Duration within tolerance but no AcoustID confirmation.</summary>
    DurationOnly,
    /// <summary>Chromaprint/AcoustID resolved to the requested recording.</summary>
    FingerprintMatch,
}

/// <summary>
/// The song quality rubric (RFC-001 §5):
/// Q = 0.25·audio + 0.25·lyric + 0.20·pitch + 0.15·video + 0.15·metadata, tiered at 0.90 / 0.75 / 0.60.
/// Note that a song without a usable video scores at most 0.85 and so tops out at tier A.
/// </summary>
public static class QualityScoring
{
    public const double AudioWeight = 0.25;
    public const double LyricWeight = 0.25;
    public const double PitchWeight = 0.20;
    public const double VideoWeight = 0.15;
    public const double MetadataWeight = 0.15;

    public const double APlusThreshold = 0.90;
    public const double AThreshold = 0.75;
    public const double BThreshold = 0.60;

    /// <summary>A voiced syllable counts towards the pitch score when its periodicity confidence exceeds this.</summary>
    public const double PitchConfidenceThreshold = 0.75;

    /// <summary>Largest allowed difference between master audio and the Spotify duration.</summary>
    public const int DurationToleranceMs = 2000;

    public static QualityAssessment Assess(QualityMetrics metrics)
    {
        foreach (var (name, value) in new[]
                 {
                     (nameof(metrics.AudioMatch), metrics.AudioMatch), (nameof(metrics.LyricAlignment), metrics.LyricAlignment),
                     (nameof(metrics.PitchConfidence), metrics.PitchConfidence), (nameof(metrics.VideoMatch), metrics.VideoMatch),
                     (nameof(metrics.MetadataConfidence), metrics.MetadataConfidence),
                 })
        {
            if (double.IsNaN(value) || value < 0 || value > 1)
                throw new ArgumentOutOfRangeException(nameof(metrics), $"{name} must be in [0, 1], was {value}.");
        }

        var raw = AudioWeight * metrics.AudioMatch
                  + LyricWeight * metrics.LyricAlignment
                  + PitchWeight * metrics.PitchConfidence
                  + VideoWeight * metrics.VideoMatch
                  + MetadataWeight * metrics.MetadataConfidence;

        // Rounded before tiering, so a stored score and its tier can never disagree
        // (e.g. a raw 0.89999999 that prints as 0.900 but tiers as A).
        var score = Math.Round(raw, 3, MidpointRounding.AwayFromZero);
        return new QualityAssessment(score, TierFor(score), metrics);
    }

    public static QualityTier TierFor(double score) => score switch
    {
        >= APlusThreshold => QualityTier.APlus,
        >= AThreshold => QualityTier.A,
        >= BThreshold => QualityTier.B,
        _ => QualityTier.ReviewRequired,
    };

    public static double AudioMatchScore(AudioMatchKind kind) => kind switch
    {
        AudioMatchKind.FingerprintMatch => 1.0,
        AudioMatchKind.DurationOnly => 0.6,
        _ => 0.0,
    };

    /// <summary>True when a candidate's length is close enough to the reference (Spotify) duration.</summary>
    public static bool IsDurationMatch(int candidateMs, int referenceMs) =>
        Math.Abs(candidateMs - referenceMs) <= DurationToleranceMs;

    /// <summary>Share of voiced syllables whose pitch confidence is above <see cref="PitchConfidenceThreshold"/>.</summary>
    public static double PitchScore(IEnumerable<double> voicedSyllableConfidences)
    {
        int total = 0, confident = 0;
        foreach (var c in voicedSyllableConfidences)
        {
            total++;
            if (c > PitchConfidenceThreshold) confident++;
        }
        return total == 0 ? 0.0 : (double)confident / total;
    }

    /// <summary>One third each for an ISRC, a known tempo and cover art.</summary>
    public static double MetadataScore(bool hasIsrc, bool hasTempo, bool hasCoverArt) =>
        ((hasIsrc ? 1 : 0) + (hasTempo ? 1 : 0) + (hasCoverArt ? 1 : 0)) / 3.0;

    /// <summary>The median of the per-word alignment confidences, or 0 when nothing aligned.</summary>
    public static double LyricScore(IEnumerable<double> wordConfidences)
    {
        var sorted = wordConfidences.Order().ToArray();
        if (sorted.Length == 0) return 0.0;
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
