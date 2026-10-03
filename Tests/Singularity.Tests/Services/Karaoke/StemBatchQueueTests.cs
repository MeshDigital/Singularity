using Microsoft.Extensions.Logging.Abstractions;
using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Library;
using Singularity.Services.Karaoke;
using Xunit;

namespace Singularity.Tests.Services.Karaoke;

public sealed class StemBatchQueueTests : IDisposable
{
    private readonly string _log = Path.Combine(Path.GetTempPath(), $"singularity-batch-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        if (File.Exists(_log)) File.Delete(_log);
    }

    private static SongEntry Song(string name) => new(
        $"{name}.txt",
        new UltraStarSong { Title = name, Artist = "a", AudioFile = "x.mp3", Bpm = 300, Voices = new[] { new UltraStarVoice(new[] { new UltraStarNote(NoteType.Regular, 0, 1, 60, "x") }) } },
        $"{name}.mp3", null, null, null, Array.Empty<string>());

    /// <summary>Separates by recording the song; "bad" songs throw; each separation takes <see cref="Delay"/>.</summary>
    private sealed class FakeSeparator : IStemSeparator
    {
        public HashSet<string> Done { get; } = new();
        public List<string> Attempts { get; } = new();
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasStems(SongEntry entry) { lock (Done) return Done.Contains(entry.Song.Title); }

        public async Task SeparateAsync(SongEntry entry, IProgress<double>? progress, CancellationToken ct)
        {
            lock (Attempts) Attempts.Add(entry.Song.Title);
            Started.TrySetResult();
            if (entry.Song.Title.StartsWith("bad")) throw new InvalidDataException("corrupt mp3");
            await Task.Delay(Delay, ct);
            lock (Done) Done.Add(entry.Song.Title);
        }
    }

    private StemBatchQueue Queue(FakeSeparator separator) =>
        new(separator, NullLogger<StemBatchQueue>.Instance, _log) { RestBetweenSongs = TimeSpan.Zero };

    private static async Task Finished(StemBatchQueue queue)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (queue.Status.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.False(queue.Status.IsRunning, "batch did not finish");
    }

    [Fact]
    public async Task SkipsSongsThatAlreadyHaveStems_SoItCanResume()
    {
        var separator = new FakeSeparator();
        separator.Done.Add("one");
        var queue = Queue(separator);

        queue.Start(new[] { Song("one"), Song("two"), Song("three") });
        await Finished(queue);

        Assert.Equal(new[] { "two", "three" }, separator.Attempts);
        Assert.Equal((2, 1, 0), (queue.Status.Done, queue.Status.Skipped, queue.Status.Failed));
    }

    [Fact]
    public async Task AFailingSong_IsLoggedAndTheRestCarriesOn()
    {
        var separator = new FakeSeparator();
        var queue = Queue(separator);

        queue.Start(new[] { Song("bad-one"), Song("good") });
        await Finished(queue);

        Assert.Equal((1, 1), (queue.Status.Done, queue.Status.Failed));
        var log = await File.ReadAllTextAsync(_log);
        Assert.Contains("bad-one.txt", log);
        Assert.Contains("corrupt mp3", log);
    }

    [Fact]
    public async Task HoldWhileSinging_AbandonsTheSongInProgress_AndRedoesItAfterRelease()
    {
        var separator = new FakeSeparator { Delay = TimeSpan.FromSeconds(30) };
        var queue = Queue(separator);
        queue.Start(new[] { Song("long") });
        await separator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        queue.Hold(); // a song starts
        await Task.Delay(100);
        Assert.True(queue.Status.IsHeld);
        Assert.Null(queue.Status.Current); // the GPU is free while held

        separator.Delay = TimeSpan.Zero;
        queue.Release(); // back to song select
        await Finished(queue);

        Assert.Equal(new[] { "long", "long" }, separator.Attempts);
        Assert.Equal((1, 0), (queue.Status.Done, queue.Status.Failed)); // an interruption is not a failure
    }

    [Fact]
    public async Task Stop_EndsTheBatch()
    {
        var separator = new FakeSeparator { Delay = TimeSpan.FromSeconds(30) };
        var queue = Queue(separator);
        queue.Start(new[] { Song("a"), Song("b") });
        await separator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        queue.Stop();
        await Finished(queue);

        Assert.Equal(new[] { "a" }, separator.Attempts);
        Assert.Equal(0, queue.Status.Done);
    }

    [Fact]
    public async Task UserPause_WaitsUntilResumed()
    {
        var separator = new FakeSeparator();
        var queue = Queue(separator);
        queue.SetPaused(true);
        queue.Start(new[] { Song("a") });
        await Task.Delay(150);
        Assert.Empty(separator.Attempts);

        queue.SetPaused(false);
        await Finished(queue);
        Assert.Equal(1, queue.Status.Done);
    }
}
