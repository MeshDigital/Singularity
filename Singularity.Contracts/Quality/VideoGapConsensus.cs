namespace Singularity.Contracts.Quality;

/// <summary>Outcome of correlating the music video's audio against the master audio.</summary>
/// <param name="IsValid">All windows agreed, so the video follows the album arrangement.</param>
/// <param name="VideoGapMs">Median offset when valid; 0 otherwise (the player then shows cover art).</param>
/// <param name="DriftMsPerMinute">How much the offset grows per minute of song when the video runs at a slightly different speed (0 if not measured).</param>
public sealed record VideoGapResult(bool IsValid, int VideoGapMs, double DriftMsPerMinute = 0)
{
    /// <summary>The sub-score fed into <see cref="QualityScoring"/>: 1 when the structure matched, else 0.</summary>
    public double Score => IsValid ? 1.0 : 0.0;
}

/// <summary>
/// The multi-window rule (RFC-001 §4A): offsets measured at ~20 %, 50 % and 80 % of the track must
/// agree pairwise within <see cref="ToleranceMs"/>; otherwise the video has a different arrangement
/// (spoken intro, radio edit, extended solo) and a single offset can't sync it.
/// </summary>
public static class VideoGapConsensus
{
    public const int ToleranceMs = 15;
    public const int MinimumWindows = 3;

    /// <param name="windowOffsetsMs">Offset found in each window, in track order. A null entry means that window had no correlation peak.</param>
    public static VideoGapResult Evaluate(IReadOnlyList<int?> windowOffsetsMs)
    {
        if (windowOffsetsMs.Count < MinimumWindows || windowOffsetsMs.Any(o => o is null))
            return new VideoGapResult(false, 0);

        var offsets = windowOffsetsMs.Select(o => o!.Value).ToArray();
        for (int i = 1; i < offsets.Length; i++)
        {
            if (Math.Abs(offsets[i] - offsets[i - 1]) >= ToleranceMs)
                return new VideoGapResult(false, 0);
        }

        var sorted = offsets.Order().ToArray();
        int mid = sorted.Length / 2;
        int median = sorted.Length % 2 == 1 ? sorted[mid] : (int)Math.Round((sorted[mid - 1] + sorted[mid]) / 2.0, MidpointRounding.AwayFromZero);
        return new VideoGapResult(true, median);
    }

    /// <summary>A video running this much off the master's speed drifts out of sync too far to fix with one offset.</summary>
    public const double MaxDriftMsPerMinute = 100;

    /// <summary>
    /// The drift-tolerant rule for real videos: their soundtrack often runs a little faster or slower than
    /// the master (resampled or re-timed, typically 50-80 ms per minute), so offsets measured along the
    /// song lie on a sloped line rather than at one value. The found offsets must fit a straight line
    /// within <see cref="ToleranceMs"/> (a different arrangement jumps by seconds), the slope must stay
    /// under <see cref="MaxDriftMsPerMinute"/>, and at least <see cref="MinimumWindows"/> windows must
    /// have found a match. With four or more matches, one window that doesn't fit may be left out. The gap is the line's value mid-song, which halves the worst error at either end.
    /// </summary>
    /// <param name="windows">Each window's position in the master and the offset it found (null: no match).</param>
    public static VideoGapResult EvaluateWithDrift(IReadOnlyList<(double AtMs, int? OffsetMs)> windows)
    {
        var found = windows.Where(w => w.OffsetMs is not null).Select(w => (T: w.AtMs, O: (double)w.OffsetMs!.Value)).ToArray();
        if (found.Length < MinimumWindows) return new VideoGapResult(false, 0);
        if (FitLine(found) is { } all) return all;

        // One stray window (a weak spurious peak, or an outro the video cuts differently) is forgiven when
        // all the others still fit, and enough remain. A real structural change - an inserted section - shifts
        // every window after it, so it leaves at least two on each side and still fails.
        if (found.Length - 1 < MinimumWindows + 1) return new VideoGapResult(false, 0);
        for (int skip = 0; skip < found.Length; skip++)
        {
            if (FitLine(found.Where((_, i) => i != skip).ToArray()) is { } rest) return rest;
        }
        return new VideoGapResult(false, 0);
    }

    private static VideoGapResult? FitLine((double T, double O)[] found)
    {
        double meanT = found.Average(w => w.T), meanO = found.Average(w => w.O);
        double sxx = found.Sum(w => (w.T - meanT) * (w.T - meanT));
        double slope = sxx > 0 ? found.Sum(w => (w.T - meanT) * (w.O - meanO)) / sxx : 0;
        if (found.Any(w => Math.Abs(w.O - (meanO + slope * (w.T - meanT))) >= ToleranceMs)) return null;

        double driftPerMinute = slope * 60_000;
        if (Math.Abs(driftPerMinute) > MaxDriftMsPerMinute) return null;

        // meanO is the fitted line's value at the windows' mean position, i.e. mid-song.
        return new VideoGapResult(true, (int)Math.Round(meanO, MidpointRounding.AwayFromZero), Math.Round(driftPerMinute, 1));
    }
}
