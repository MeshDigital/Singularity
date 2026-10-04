using Singularity.Karaoke.Scoring;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class HighScoreTableTests
{
    private static readonly DateTime T0 = new(2026, 10, 4, 20, 0, 0, DateTimeKind.Utc);
    private static readonly string Song = HighScoreTable.SongKey("The Killers", "Mr. Brightside");

    private static HighScore Score(int score, string singer = "Anna", string difficulty = "Medium", int minutes = 0) =>
        new(Song, difficulty, singer, score, T0.AddMinutes(minutes));

    [Fact]
    public void Places_AreCountedPerSongAndDifficulty()
    {
        var table = new HighScoreTable();

        Assert.Equal(1, table.Add(Score(6000)));
        Assert.Equal(1, table.Add(Score(8000, "Bram", minutes: 1)));
        Assert.Equal(3, table.Add(Score(5000, minutes: 2)));
        Assert.Equal(1, table.Add(Score(4000, difficulty: "Hard", minutes: 3)));

        Assert.Equal(new[] { 8000, 6000, 5000 }, table.Top(Song, "Medium").Select(s => s.Score));
        Assert.Equal(8000, table.Best(Song)!.Score);
    }

    [Fact]
    public void OnlyTheTopTenAreKept_AndZeroNeverCounts()
    {
        var table = new HighScoreTable();
        for (int i = 1; i <= HighScoreTable.Kept; i++) table.Add(Score(i * 1000, minutes: i));

        Assert.Equal(0, table.Add(Score(500, minutes: 20)));
        Assert.Equal(0, table.Add(Score(0, minutes: 21)));
        Assert.Equal(HighScoreTable.Kept, table.All.Count);
        Assert.Equal(1, table.Add(Score(20_000, minutes: 22)));
        Assert.Equal(HighScoreTable.Kept, table.All.Count);
        Assert.DoesNotContain(table.All, s => s.Score == 1000);
    }

    [Fact]
    public void VersionsOfASong_ShareTheirScores() =>
        Assert.Equal(HighScoreTable.SongKey("Shakira ft. Freshlyground", "Waka Waka (This Time for Africa)"), HighScoreTable.SongKey("Shakira", "Waka Waka"));

    [Fact]
    public void ATie_GoesToWhoSangItFirst()
    {
        var table = new HighScoreTable();
        table.Add(Score(7000, "Anna", minutes: 0));

        Assert.Equal(2, table.Add(Score(7000, "Bram", minutes: 5)));
        Assert.Equal("Anna", table.Top(Song, "Medium")[0].Singer);
    }
}
