namespace Singularity.Contracts.Quality;

/// <summary>Outcome of correlating the music video's audio against the master audio.</summary>
/// <param name="IsValid">All windows agreed, so the video follows the album arrangement.</param>
/// <param name="VideoGapMs">Median offset when valid; 0 otherwise (the player then shows cover art).</param>
public sealed record VideoGapResult(bool IsValid, int VideoGapMs)
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
}
