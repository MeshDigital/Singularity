using Microsoft.Extensions.Logging.Abstractions;
using Singularity.Services.Karaoke.Ingest;
using Xunit;

namespace Singularity.Tests.Services.Karaoke;

public sealed class IngestQueueTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("singularity-ingestq-");
    private readonly FakeAnalyzer _analyzer = new();
    private readonly IngestQueue _queue;
    private readonly List<string> _ready = new();

    public IngestQueueTests()
    {
        var packager = new KaraokePackager(_analyzer, new FakeLyrics(), new FakeMedia(), Path.Combine(_root.FullName, "staging"), NullLogger.Instance);
        _queue = new IngestQueue(packager, () => Path.Combine(_root.FullName, "songs"), NullLogger.Instance);
        _queue.PackageReady += folder => { lock (_ready) _ready.Add(folder); };
    }

    public void Dispose() => _root.Delete(recursive: true);

    private IngestSource Source(string title)
    {
        var audio = Path.Combine(_root.FullName, title + ".mp3");
        File.WriteAllBytes(audio, new byte[] { 1 });
        return new IngestSource(audio, "Artist", title, TrackId: "spotify:track:" + title);
    }

    private IngestItem Item(string key) => _queue.Items.Single(i => i.Key == key);

    [Fact]
    public async Task DownloadStates_ThenBuild_EndReady()
    {
        _queue.Track("a", "Artist", "One");
        _queue.Track("a", "Artist", "One", IngestState.Downloading);
        Assert.Equal(IngestState.Downloading, Item("a").State);

        _queue.Enqueue("a", Source("One"));
        await _queue.WhenIdleAsync();

        var item = Item("a");
        Assert.Equal(IngestState.Ready, item.State);
        Assert.Equal(Path.Combine(_root.FullName, "songs", "Artist - One"), item.PackageFolder);
        Assert.NotNull(item.Tier);
        Assert.Equal(new[] { item.PackageFolder }, _ready);

        // A late download event doesn't undo it.
        _queue.Track("a", "Artist", "One", IngestState.Downloading);
        Assert.Equal(IngestState.Ready, Item("a").State);
    }

    [Fact]
    public async Task AFailure_IsShownAndTheNextSongStillBuilds()
    {
        _analyzer.Fail = true;
        _queue.Enqueue("a", Source("One"));
        await _queue.WhenIdleAsync();
        _analyzer.Fail = false;
        _queue.Enqueue("b", Source("Two"));
        await _queue.WhenIdleAsync();

        Assert.Equal(IngestState.Failed, Item("a").State);
        Assert.Equal("worker failed", Item("a").Detail);
        Assert.Equal(IngestState.Ready, Item("b").State);
    }

    [Fact]
    public async Task Hold_StopsTheSongBeingBuilt_AndReleaseStartsItOver()
    {
        var started = new TaskCompletionSource();
        _analyzer.Gate = async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        _queue.Enqueue("a", Source("One"));
        _queue.Enqueue("b", Source("Two"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _queue.Hold();
        await WaitFor(() => Item("a").State == IngestState.InQueue);
        await Task.Delay(100);
        Assert.Equal(1, _analyzer.Runs); // nothing new starts while held
        Assert.Equal(IngestState.InQueue, Item("b").State);

        _analyzer.Gate = null;
        _queue.Release();
        await _queue.WhenIdleAsync();

        Assert.Equal(IngestState.Ready, Item("a").State);
        Assert.Equal(IngestState.Ready, Item("b").State);
        Assert.Equal(3, _analyzer.Runs);
        Assert.Equal(new[] { "One", "Two" }, _ready.Select(f => Path.GetFileName(f)!.Replace("Artist - ", "")));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
