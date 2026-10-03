using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Singularity.Karaoke.Library;

namespace Singularity.Services.Karaoke;

/// <summary>
/// Where a song's separated stems (vocals + instrumental) are. A chart's own #VOCALS / #INSTRUMENTAL
/// files win. Otherwise stems made by the inference worker live in a cache under
/// %LOCALAPPDATA%\Singularity\stems, keyed by the audio file, because song folders are never written to.
/// </summary>
public sealed class StemStore
{
    public const string VocalsFileName = "vocals.wav";
    public const string InstrumentalFileName = "instrumental.wav";

    public StemStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Singularity", "stems"))
    {
    }

    public StemStore(string cacheRoot) => CacheRoot = cacheRoot;

    public string CacheRoot { get; }

    /// <summary>The song's stems, or null when it has none yet.</summary>
    public (string Vocals, string Instrumental)? Find(SongEntry entry)
    {
        var song = entry.Song;
        if (song.VocalsFile is { } v && song.InstrumentalFile is { } i)
        {
            string vocals = Path.Combine(entry.Folder, v), instrumental = Path.Combine(entry.Folder, i);
            if (File.Exists(vocals) && File.Exists(instrumental)) return (vocals, instrumental);
        }
        if (entry.AudioPath is null) return null;
        var folder = CacheFolderFor(entry.AudioPath);
        string cachedVocals = Path.Combine(folder, VocalsFileName), cachedInstrumental = Path.Combine(folder, InstrumentalFileName);
        return File.Exists(cachedVocals) && File.Exists(cachedInstrumental) ? (cachedVocals, cachedInstrumental) : null;
    }

    /// <summary>Cache folder for an audio file's stems; a changed file (size or date) gets a new folder.</summary>
    public string CacheFolderFor(string audioPath)
    {
        var info = new FileInfo(audioPath);
        var key = $"{info.FullName.ToLowerInvariant()}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(audioPath);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(CacheRoot, $"{(name.Length > 40 ? name[..40] : name)}-{hash}");
    }
}
