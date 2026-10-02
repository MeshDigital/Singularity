using Singularity.Karaoke.Library;
using Singularity.Tests.Contracts;
using Xunit;

namespace Singularity.Tests.Karaoke;

public sealed class SongScannerTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("singularity-songs-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Folder(string name, string txt, params string[] files)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root.FullName, name)).FullName;
        File.WriteAllText(Path.Combine(dir, name + ".txt"), txt);
        foreach (var f in files) File.WriteAllBytes(Path.Combine(dir, f), new byte[] { 1 });
        return dir;
    }

    private static string Txt(string artist, string title, string extraHeaders = "") =>
        $"#TITLE:{title}\n#ARTIST:{artist}\n#MP3:Song.MP3\n{extraHeaders}#BPM:300\n#GAP:0\n: 0 4 0 la\nE\n";

    [Fact]
    public void FindsSongs_ResolvesFilesCaseInsensitively_AndSortsByArtist()
    {
        Folder("b", Txt("Zed", "Last", "#VIDEO:clip.mp4\n#COVER:Cover.JPG\n"), "song.mp3", "Clip.MP4", "cover.jpg");
        Folder("a", Txt("Abba", "First"), "Song.mp3");

        var result = SongScanner.Scan(new[] { _root.FullName });

        Assert.Equal(new[] { "Abba", "Zed" }, result.Songs.Select(s => s.Song.Artist));
        var zed = result.Songs[1];
        Assert.EndsWith("song.mp3", zed.AudioPath);
        Assert.EndsWith("Clip.MP4", zed.VideoPath);
        Assert.EndsWith("cover.jpg", zed.CoverPath);
        Assert.Empty(zed.Problems);
        Assert.True(zed.IsPlayable);
    }

    [Fact]
    public void CoverAndBackground_FallBackToNamingConventions()
    {
        Folder("c", Txt("Artist", "Song", "#COVER:missing.jpg\n"), "song.mp3", "Artist - Song [CO].jpg", "Artist - Song [BG].png");

        var song = SongScanner.Scan(new[] { _root.FullName }).Songs.Single();

        Assert.EndsWith("[CO].jpg", song.CoverPath);
        Assert.EndsWith("[BG].png", song.BackgroundPath);
        Assert.Contains("cover file not found: missing.jpg", song.Problems);
    }

    [Fact]
    public void MissingAudio_IsListedButNotPlayable()
    {
        Folder("d", Txt("Artist", "Silent"));
        var song = SongScanner.Scan(new[] { _root.FullName }).Songs.Single();
        Assert.False(song.IsPlayable);
        Assert.Contains("audio file not found: Song.MP3", song.Problems);
    }

    [Fact]
    public void UnreadableTxt_IsAFailureNotAnException()
    {
        Folder("e", "#TITLE:no artist\n: 0 1 0 x\nE\n", "song.mp3");
        Folder("f", Txt("Fine", "Song"), "song.mp3");

        var result = SongScanner.Scan(new[] { _root.FullName, Path.Combine(_root.FullName, "does-not-exist") });

        Assert.Single(result.Songs);
        Assert.Contains("#ARTIST", Assert.Single(result.Failures).Error);
    }

    [RequiresSongCollectionFact]
    public void RealCollection_ScansWithoutFailures()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = SongScanner.Scan(new[] { UltraStarCorpusTests.SongsDir! });
        Assert.Empty(result.Failures);
        Assert.True(result.Songs.Count(s => s.IsPlayable) >= result.Songs.Count - 5);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"scan took {sw.Elapsed}");
    }
}
