using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Library;
using Singularity.Services.Karaoke;
using Xunit;

namespace Singularity.Tests.Services.Karaoke;

public sealed class StemStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("singularity-stems-");

    public void Dispose() => _dir.Delete(recursive: true);

    private SongEntry Entry(string? vocals = null, string? instrumental = null)
    {
        var songDir = Directory.CreateDirectory(Path.Combine(_dir.FullName, "song")).FullName;
        var audio = Path.Combine(songDir, "song.mp3");
        if (!File.Exists(audio)) File.WriteAllBytes(audio, new byte[] { 1, 2, 3 });
        var song = new UltraStarSong { Title = "t", Artist = "a", AudioFile = "song.mp3", Bpm = 300, VocalsFile = vocals, InstrumentalFile = instrumental };
        return new SongEntry(Path.Combine(songDir, "song.txt"), song, audio, null, null, null, Array.Empty<string>());
    }

    [Fact]
    public void NoStems_IsNull() => Assert.Null(new StemStore(Path.Combine(_dir.FullName, "cache")).Find(Entry()));

    [Fact]
    public void ChartStems_AreUsedWhenTheFilesExist()
    {
        var entry = Entry("v.wav", "i.wav");
        var store = new StemStore(Path.Combine(_dir.FullName, "cache"));
        Assert.Null(store.Find(entry)); // headers but no files
        File.WriteAllBytes(Path.Combine(entry.Folder, "v.wav"), new byte[1]);
        File.WriteAllBytes(Path.Combine(entry.Folder, "i.wav"), new byte[1]);
        Assert.Equal((Path.Combine(entry.Folder, "v.wav"), Path.Combine(entry.Folder, "i.wav")), store.Find(entry));
    }

    [Fact]
    public void CachedStems_AreFound_AndAChangedAudioFileGetsANewFolder()
    {
        var entry = Entry();
        var store = new StemStore(Path.Combine(_dir.FullName, "cache"));
        var folder = Directory.CreateDirectory(store.CacheFolderFor(entry.AudioPath!)).FullName;
        File.WriteAllBytes(Path.Combine(folder, StemStore.VocalsFileName), new byte[1]);
        File.WriteAllBytes(Path.Combine(folder, StemStore.InstrumentalFileName), new byte[1]);
        Assert.NotNull(store.Find(entry));
        Assert.StartsWith(store.CacheRoot, folder);

        File.WriteAllBytes(entry.AudioPath!, new byte[] { 9, 9, 9, 9 }); // re-downloaded: different size
        Assert.NotEqual(folder, store.CacheFolderFor(entry.AudioPath!));
        Assert.Null(store.Find(entry));
    }
}
