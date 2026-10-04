using Singularity.Karaoke.Library;

namespace Singularity.Karaoke.Scoring;

/// <summary>One sung score worth keeping.</summary>
/// <param name="Song">The song, as versions are grouped (<see cref="HighScoreTable.SongKey"/>).</param>
public sealed record HighScore(string Song, string Difficulty, string Singer, int Score, DateTime At);

/// <summary>
/// The best scores per song and difficulty, as UltraStar keeps them: the top <see cref="Kept"/> of each.
/// A song is matched the way versions are grouped (artist and title without version notes), so a
/// song's community and AI charts share one table.
/// </summary>
public sealed class HighScoreTable
{
    public const int Kept = 10;

    private readonly List<HighScore> _scores;

    public HighScoreTable(IEnumerable<HighScore>? scores = null) => _scores = scores?.ToList() ?? new();

    public IReadOnlyList<HighScore> All => _scores;

    public static string SongKey(string artist, string title) => SongClusters.KeyOf(artist, title);

    /// <summary>
    /// Adds a score; returns its place among the song's scores at this difficulty (1 = the best), or 0
    /// when it doesn't make the top <see cref="Kept"/> (it isn't kept then). A zero score is never kept.
    /// </summary>
    public int Add(HighScore score)
    {
        if (score.Score <= 0) return 0;
        _scores.Add(score);
        var ranked = Ranked(score.Song, score.Difficulty).ToList();
        foreach (var dropped in ranked.Skip(Kept)) _scores.Remove(dropped);
        int place = ranked.IndexOf(score) + 1;
        return place <= Kept ? place : 0;
    }

    /// <summary>The best scores for a song at a difficulty, best first (earlier first on a tie).</summary>
    public IReadOnlyList<HighScore> Top(string song, string difficulty, int count = 5) => Ranked(song, difficulty).Take(count).ToList();

    /// <summary>The song's best score at any difficulty; null when it was never sung.</summary>
    public HighScore? Best(string song) =>
        _scores.Where(s => s.Song == song).OrderByDescending(s => s.Score).ThenBy(s => s.At).FirstOrDefault();

    private IEnumerable<HighScore> Ranked(string song, string difficulty) =>
        _scores.Where(s => s.Song == song && s.Difficulty == difficulty).OrderByDescending(s => s.Score).ThenBy(s => s.At);
}
