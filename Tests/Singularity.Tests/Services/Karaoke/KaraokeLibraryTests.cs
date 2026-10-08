using Microsoft.Extensions.Logging.Abstractions;
using Singularity.Configuration;
using Singularity.Services.Karaoke;
using Xunit;

namespace Singularity.Tests.Services.Karaoke;

public sealed class KaraokeLibraryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("singularity-library-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Song(string under, string artist, string title)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root.FullName, under, $"{artist} - {title}")).FullName;
        File.WriteAllText(Path.Combine(dir, "song.txt"), $"#TITLE:{title}\n#ARTIST:{artist}\n#MP3:a.mp3\n#BPM:300\n#GAP:0\n: 0 4 0 la\nE\n");
        File.WriteAllBytes(Path.Combine(dir, "a.mp3"), new byte[] { 1 });
        return dir;
    }

    [Fact]
    public async Task HasSong_KnowsTheCommunityCollection_AndOptionallyImportedSongs()
    {
        Song("community", "Shakira ft. Freshlyground", "Waka Waka (This Time for Africa)");
        Song("made", "The Killers", "Mr. Brightside");
        var config = new AppConfig
        {
            KaraokeSongFolders = Path.Combine(_root.FullName, "community"),
            KaraokeIngestFolder = Path.Combine(_root.FullName, "made"),
        };
        var library = new KaraokeLibrary(config, NullLogger<KaraokeLibrary>.Instance);

        // The same song under Spotify's names counts; scans by itself the first time.
        Assert.True(await library.HasSongAsync("Shakira", "Waka Waka (This Time for Africa)", includeImported: false));
        Assert.False(await library.HasSongAsync("The Killers", "Mr. Brightside", includeImported: false));
        Assert.True(await library.HasSongAsync("The Killers", "Mr. Brightside", includeImported: true));
        Assert.False(await library.HasSongAsync("Queen", "Bohemian Rhapsody", includeImported: true));
    }

    [Fact]
    public void RemovingEveryFolder_IsRespected_TheUsualFolderDoesNotComeBack()
    {
        var made = Path.Combine(_root.FullName, "made");
        Directory.CreateDirectory(made);
        var none = new KaraokeLibrary(new AppConfig { KaraokeSongFolders = KaraokeLibrary.NoFoldersChosen, KaraokeIngestFolder = made },
            NullLogger<KaraokeLibrary>.Instance);
        Assert.DoesNotContain(KaraokeLibrary.DefaultCollectionFolder, none.Folders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(made, none.Folders, StringComparer.OrdinalIgnoreCase);

        // Nothing set at all: the usual folder is found by itself (where this machine has one).
        var unset = new KaraokeLibrary(new AppConfig { KaraokeIngestFolder = made }, NullLogger<KaraokeLibrary>.Instance);
        Assert.Equal(Directory.Exists(KaraokeLibrary.DefaultCollectionFolder),
            unset.Folders.Contains(KaraokeLibrary.DefaultCollectionFolder, StringComparer.OrdinalIgnoreCase));
    }
}
