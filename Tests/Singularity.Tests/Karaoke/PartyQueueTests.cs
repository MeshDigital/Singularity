using Singularity.Karaoke.Party;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class PartyQueueTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"party-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }

    [Fact]
    public void SongsQueueInOrder_AndAreTakenFromTheTop()
    {
        var q = new PartyQueue();
        var a = q.Add(@"D:\songs\A", "Song A", "Band", "Alice").Added!;
        var b = q.Add(@"D:\songs\B", "Song B", "Band", "Bob").Added!;

        Assert.Equal(a, q.Next);
        Assert.Equal(a, q.Take(a.Id));
        Assert.Equal(b, q.Next);
    }

    [Fact]
    public void ASingerCannotQueueTheSameSongTwice_OrMoreThanThree()
    {
        var q = new PartyQueue();
        Assert.NotNull(q.Add("1", "One", "x", "Alice").Added);
        Assert.Contains("already in the queue", q.Add("1", "One", "x", "alice").Refused);
        q.Add("2", "Two", "x", "Alice");
        q.Add("3", "Three", "x", "Alice");
        Assert.Contains("already has 3", q.Add("4", "Four", "x", "Alice").Refused);
        Assert.NotNull(q.Add("4", "Four", "x", "Bob").Added); // others still can
    }

    [Fact]
    public void ANameIsNeeded_AndCleaned()
    {
        var q = new PartyQueue();
        Assert.NotNull(q.Add("1", "One", "x", "   ").Refused);
        Assert.Equal("Alice", q.Add("1", "One", "x", "  Ali\u0000ce ").Added!.Singer);
        Assert.Equal(PartyQueue.MaxNameLength, PartyQueue.CleanName(new string('x', 100)).Length);
    }

    [Fact]
    public void MoveUp_AndRemove()
    {
        var q = new PartyQueue();
        var a = q.Add("1", "One", "x", "Alice").Added!;
        var b = q.Add("2", "Two", "x", "Bob").Added!;

        Assert.True(q.MoveUp(b.Id));
        Assert.Equal(b, q.Next);
        Assert.True(q.Remove(b.Id));
        Assert.Equal(a, q.Next);
        Assert.False(q.MoveUp(a.Id)); // already first
    }

    [Fact]
    public void TheQueueSurvivesARestart()
    {
        var first = new PartyQueue(_file);
        first.Add(@"D:\songs\A", "Song A", "Band", "Alice", from: "phone");

        var again = new PartyQueue(_file);

        var only = Assert.Single(again.Items);
        Assert.Equal(("Song A", "Alice", "phone"), (only.Title, only.Singer, only.From));
    }

    [Fact]
    public void Changes_AreAnnounced()
    {
        var q = new PartyQueue();
        int changes = 0;
        q.Changed += () => changes++;
        var a = q.Add("1", "One", "x", "Alice").Added!;
        q.Take(a.Id);
        Assert.Equal(2, changes);
    }
}
