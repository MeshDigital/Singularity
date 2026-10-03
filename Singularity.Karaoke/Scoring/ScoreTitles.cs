namespace Singularity.Karaoke.Scoring;

/// <summary>The title shown with a final score on the results screen.</summary>
public static class ScoreTitles
{
    private static readonly (int Min, string Title)[] Tiers =
    {
        (9000, "Karaoke God"),
        (7500, "Lead Singer"),
        (6000, "Rising Star"),
        (4000, "Wannabe"),
        (2000, "Amateur"),
        (0, "Tone Deaf"),
    };

    public static string For(int score) => Tiers.First(t => score >= t.Min).Title;
}

/// <summary>The results screen's breakdown, in tens like the total, always adding up to it exactly.</summary>
public readonly record struct DisplayedScore(int Notes, int Golden, int LineBonus, int Total)
{
    /// <summary>
    /// Rounds each part down to tens, then gives the tens lost to rounding to the parts with the
    /// largest remainders (largest-remainder method), so the parts sum to <see cref="ScoreBreakdown.Total"/>.
    /// </summary>
    public static DisplayedScore From(ScoreBreakdown score)
    {
        double[] parts = { score.Notes, score.Golden, score.LineBonus };
        int[] tens = parts.Select(p => (int)Math.Floor(p / 10)).ToArray();
        int missing = score.Total / 10 - tens.Sum();
        foreach (int i in Enumerable.Range(0, 3).OrderByDescending(i => parts[i] / 10 - tens[i]).Take(Math.Max(0, missing)))
            tens[i]++;
        return new DisplayedScore(tens[0] * 10, tens[1] * 10, tens[2] * 10, score.Total);
    }
}
