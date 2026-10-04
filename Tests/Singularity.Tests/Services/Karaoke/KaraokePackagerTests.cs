using Microsoft.Extensions.Logging.Abstractions;
using Singularity.Contracts.Inference;
using Singularity.Contracts.Quality;
using Singularity.Contracts.Song;
using Singularity.Contracts.UltraStar;
using Singularity.Services.Karaoke.Ingest;
using Singularity.Tests.Karaoke;
using Xunit;

namespace Singularity.Tests.Services.Karaoke;

public sealed class KaraokePackagerTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("singularity-ingest-");
    private string Staging => Path.Combine(_root.FullName, "staging");
    private string Output => Path.Combine(_root.FullName, "songs");
    private readonly FakeMedia _media = new();
    private readonly FakeAnalyzer _analyzer = new();
    private readonly FakeLyrics _lyrics = new();

    public void Dispose() => _root.Delete(recursive: true);

    private KaraokePackager Packager() => new(_analyzer, _lyrics, _media, Staging, NullLogger.Instance);

    private string Download(string name = "Mr. Brightside.flac")
    {
        var path = Path.Combine(_root.FullName, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private static IngestSource Source(string audio, string? videoPath = null, string trackId = "spotify:track:abc", int? expectedMs = 222_000) =>
        new(audio, "The Killers", "Mr. Brightside", "Hot Fuss", trackId, "USIR20400274", expectedMs, "https://art/cover.jpg", videoPath);

    [Fact]
    public async Task BuildsAFinishedPackage_AndLeavesNoStagingBehind()
    {
        var audio = Download();

        var result = await Packager().BuildAsync(Source(audio), Output);

        Assert.Equal(Path.Combine(Output, "The Killers - Mr. Brightside"), result.PackageFolder);
        var song = UltraStarSerializer.ReadFile(Path.Combine(result.PackageFolder, SongPackage.UltraStarFileName));
        Assert.Equal("Mr. Brightside", song.Title);
        Assert.Equal("audio.flac", song.AudioFile);
        Assert.Equal(SongPackage.VocalsFileName, song.VocalsFile);
        Assert.Equal(SongPackage.InstrumentalFileName, song.InstrumentalFile);
        Assert.Equal(SongPackage.CoverFileName, song.CoverFile);
        Assert.Null(song.VideoFile);
        Assert.Equal("English", song.Language);
        Assert.Equal(KaraokePackager.Creator, song.Creator);
        Assert.Equal(4, song.Voices.Single().Notes.Count(n => n.Type != NoteType.LineBreak));
        foreach (var f in new[] { "audio.flac", "vocals.wav", "instrumental.wav", "cover.jpg", "metadata.json" })
            Assert.True(File.Exists(Path.Combine(result.PackageFolder, f)), f);

        var metadata = await SongPackage.ReadMetadataAsync(result.PackageFolder);
        Assert.Equal("spotify:track:abc", metadata.TrackId);
        Assert.Equal("USIR20400274", metadata.Isrc);
        Assert.Equal(222_000, metadata.DurationMs);
        Assert.False(metadata.Timing.VideoStructureValid);
        Assert.Equal(0.6, metadata.Quality.Metrics.AudioMatch);
        Assert.Equal(1.0, metadata.Quality.Metrics.MetadataConfidence);

        Assert.True(File.Exists(audio)); // the download is the user's: copied, not moved
        Assert.Empty(Directory.GetFileSystemEntries(Staging));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(Output, KaraokePackager.IncomingFolderName)));
        Assert.Equal((Lyrics: "la la\nla la", Kind: LyricsKind.Plain), (_analyzer.Last!.Lyrics, _analyzer.Last.LyricsKind));
    }

    [Fact]
    public async Task MatchingVideo_IsSyncedAndKeptWithoutItsSound()
    {
        var master = VideoSyncTests.Music(120);
        _media.Sound["audio"] = master;
        _media.Sound["clip"] = VideoSyncTests.Video(master, 3800);
        var clip = Download("clip.mp4");

        var result = await Packager().BuildAsync(Source(Download(), clip), Output);

        Assert.True(result.HasVideo);
        var song = UltraStarSerializer.ReadFile(Path.Combine(result.PackageFolder, SongPackage.UltraStarFileName));
        Assert.Equal("video.mp4", song.VideoFile);
        Assert.InRange(song.VideoGapMs, 3790, 3810);
        Assert.Equal(new[] { (clip, Path.Combine("video.mp4")) }, _media.Stripped.Select(s => (s.In, Path.GetFileName(s.Out))));
        Assert.Equal(1.0, result.Quality.Metrics.VideoMatch);
    }

    [Fact]
    public async Task AFoundVideo_IsSynced_AndNoneFoundGetsTheCover()
    {
        var master = VideoSyncTests.Music(120);
        _media.Sound["audio"] = master;
        _media.Sound[YtDlpVideoFinder.FileStem] = VideoSyncTests.Video(master, 1500);
        var videos = new FakeVideos();
        var packager = new KaraokePackager(_analyzer, _lyrics, _media, Staging, NullLogger.Instance, videos);

        var found = await packager.BuildAsync(Source(Download()), Output);
        videos.Found = false;
        var none = await packager.BuildAsync(Source(Download(), trackId: "spotify:track:other"), Output);

        Assert.True(found.HasVideo);
        Assert.Equal(("The Killers", "Mr. Brightside", 222_000), videos.Asked[0]);
        Assert.InRange(UltraStarSerializer.ReadFile(Path.Combine(found.PackageFolder, SongPackage.UltraStarFileName)).VideoGapMs, 1490, 1510);
        Assert.False(none.HasVideo);
        Assert.Contains(none.Notes, n => n.Contains("No music video"));
    }

    [Fact]
    public async Task AFailedChart_StopsTheVideoDownload_AndCleansUp()
    {
        var videos = new FakeVideos { Hang = true };
        _analyzer.Fail = true;
        var packager = new KaraokePackager(_analyzer, _lyrics, _media, Staging, NullLogger.Instance, videos);

        await Assert.ThrowsAsync<InvalidOperationException>(() => packager.BuildAsync(Source(Download()), Output));

        Assert.True(videos.WasCancelled);
        Assert.Empty(Directory.GetFileSystemEntries(Staging));
    }

    [Fact]
    public void YtDlp_SearchesFiveResults_ForOneOfAFittingLength()
    {
        var args = YtDlpVideoFinder.Arguments("Shakira", "Waka Waka", 202_657, @"C:\staging\x", ffmpeg: null);

        Assert.Equal("ytsearch5:Shakira - Waka Waka official music video", args[^1]);
        Assert.Equal("!is_live & duration >= 187 & duration <= 322", args[args.ToList().IndexOf("--match-filter") + 1]);
        Assert.Equal("1", args[args.ToList().IndexOf("--max-downloads") + 1]);
        Assert.DoesNotContain("--ffmpeg-location", args);
    }

    [Fact]
    public async Task VideoOfAnotherArrangement_IsDropped()
    {
        _media.Sound["audio"] = VideoSyncTests.Music(120, seed: 1);
        _media.Sound["clip"] = VideoSyncTests.Music(120, seed: 2);

        var result = await Packager().BuildAsync(Source(Download(), Download("clip.mp4")), Output);

        Assert.False(result.HasVideo);
        Assert.Contains(result.Notes, n => n.Contains("different version"));
        Assert.False(File.Exists(Path.Combine(result.PackageFolder, "video.mp4")));
        Assert.Equal(0.0, result.Quality.Metrics.VideoMatch);
    }

    [Fact]
    public async Task AFailedStage_LeavesNothingInTheLibrary()
    {
        _analyzer.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Packager().BuildAsync(Source(Download()), Output));

        Assert.Empty(Directory.GetFileSystemEntries(Staging));
        Assert.False(Directory.Exists(Output) && Directory.EnumerateFiles(Output, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task ReimportingTheSameTrack_ReplacesIt_ButAnotherSongOfTheSameNameIsKept()
    {
        var first = await Packager().BuildAsync(Source(Download()), Output);
        File.WriteAllText(Path.Combine(first.PackageFolder, "stale.txt"), "old");

        var again = await Packager().BuildAsync(Source(Download()), Output);
        var other = await Packager().BuildAsync(Source(Download(), trackId: "spotify:track:live"), Output);

        Assert.Equal(first.PackageFolder, again.PackageFolder);
        Assert.False(File.Exists(Path.Combine(again.PackageFolder, "stale.txt")));
        Assert.Equal(first.PackageFolder + " (2)", other.PackageFolder);
        Assert.Equal(2, Directory.GetDirectories(Output).Count(d => !Path.GetFileName(d).StartsWith('.')));
    }

    [Fact]
    public async Task DownloadOfAnotherLength_IsNotedAndScoresNoAudioMatch()
    {
        var result = await Packager().BuildAsync(Source(Download(), expectedMs: 180_000), Output);

        Assert.Contains(result.Notes, n => n.Contains("3:42") && n.Contains("3:00"));
        Assert.Equal(0.0, result.Quality.Metrics.AudioMatch);
    }

    [Fact]
    public async Task NoLyricsOnline_TheWorkerTranscribes()
    {
        _lyrics.Result = null;

        var result = await Packager().BuildAsync(Source(Download()), Output);

        Assert.Null(_analyzer.Last!.Lyrics);
        Assert.Contains(result.Notes, n => n.Contains("transcribed"));
    }

    [Theory]
    [InlineData("en", "English")]
    [InlineData("de", "German")]
    [InlineData("nl", "Dutch")]
    [InlineData("und", null)]
    [InlineData("qqq", null)]
    [InlineData(null, null)]
    public void LanguageName_IsTheEnglishName(string? code, string? name) => Assert.Equal(name, KaraokePackager.LanguageName(code));
}

internal sealed class FakeLyrics : ILyricsLookup
{
    public (string Text, LyricsKind Kind)? Result { get; set; } = ("la la\nla la", LyricsKind.Plain);

    public Task<(string Text, LyricsKind Kind)?> FindAsync(string artist, string title, string? album, int durationMs, CancellationToken ct) =>
        Task.FromResult(Result);
}

internal sealed class FakeAnalyzer : ITrackAnalyzer
{
    public bool Fail { get; set; }
    public ProcessTrackCommand? Last { get; private set; }

    /// <summary>Awaited before the analysis finishes, so a test can keep it running.</summary>
    public Func<CancellationToken, Task>? Gate { get; set; }
    public int Runs { get; private set; }

    public async Task<TrackAnalysisResult> AnalyzeAsync(ProcessTrackCommand command, IProgress<WorkerEvent>? progress, CancellationToken ct)
    {
        Last = command;
        Runs++;
        // Like the worker: stems in the output folder, then a failure or the result.
        File.WriteAllBytes(Path.Combine(command.OutputFolder, "vocals.wav"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(command.OutputFolder, "instrumental.wav"), new byte[] { 2 });
        progress?.Report(new ProgressUpdateEvent(command.TaskId, PipelineStage.Separation, 0.5));
        if (Fail) throw new InvalidOperationException("worker failed");
        if (Gate is { } gate) await gate(ct);

        TimedSyllable Syl(string text, int start, int tone) => new(text, start, start + 400, true, tone, 0.9, 0.8);
        var lines = new[]
        {
            new LyricLine(new[] { Syl("la", 10_000, 60), Syl("la", 10_500, 62) }),
            new LyricLine(new[] { Syl("la", 12_000, 64), Syl("la", 12_500, 65) }),
        };
        return new TrackAnalysisResult(
            Path.Combine(command.OutputFolder, "vocals.wav"), Path.Combine(command.OutputFolder, "instrumental.wav"),
            120, "en", lines, new Dictionary<string, string> { ["separation"] = "fake" });
    }
}

internal sealed class FakeMedia : IIngestMedia
{
    /// <summary>Decoded sound by file name (without extension); anything else has none.</summary>
    public Dictionary<string, float[]> Sound { get; } = new();
    public List<(string In, string Out)> Stripped { get; } = new();

    public Task<int> ProbeDurationMsAsync(string path, CancellationToken ct) => Task.FromResult(222_000);

    public Task<float[]> DecodeMonoAsync(string path, int sampleRate, CancellationToken ct) =>
        Task.FromResult(Sound.GetValueOrDefault(Path.GetFileNameWithoutExtension(path)) ?? Array.Empty<float>());

    public Task StripAudioAsync(string videoIn, string videoOut, CancellationToken ct)
    {
        Stripped.Add((videoIn, videoOut));
        File.Copy(videoIn, videoOut);
        return Task.CompletedTask;
    }

    public Task<byte[]?> DownloadAsync(string url, CancellationToken ct) => Task.FromResult<byte[]?>(new byte[] { 0xFF, 0xD8 });
}

internal sealed class FakeVideos : IVideoFinder
{
    public bool Found { get; set; } = true;
    public bool Hang { get; set; }
    public bool WasCancelled { get; private set; }
    public List<(string, string, int)> Asked { get; } = new();

    public async Task<string?> FindAsync(string artist, string title, int durationMs, string folder, CancellationToken ct)
    {
        Asked.Add((artist, title, durationMs));
        var path = Path.Combine(folder, YtDlpVideoFinder.FileStem + ".mp4");
        await File.WriteAllBytesAsync(path, new byte[] { 1 }, CancellationToken.None);
        if (Hang)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { WasCancelled = true; throw; }
        }
        return Found ? path : null;
    }
}
