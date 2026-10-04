using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Configuration;
using Singularity.Contracts.Song;
using Singularity.Karaoke.Library;

namespace Singularity.Services.Karaoke;

/// <summary>A music video for a recording: video position = audio position + <paramref name="GapMs"/>.</summary>
/// <param name="Synced">False for a backdrop video that isn't synced to the song.</param>
public sealed record KaraokeVideo(string Path, int GapMs, bool Synced);

/// <summary>The karaoke song collection: the configured UltraStar folders, scanned on demand (never written to).</summary>
public sealed class KaraokeLibrary
{
    /// <summary>Extra song folders for development runs (separated by ';'), on top of the configured ones.</summary>
    public const string SongsDirEnvironmentVariable = "SINGULARITY_SONGS_DIR";

    private readonly AppConfig _config;
    private readonly ILogger<KaraokeLibrary> _logger;

    public KaraokeLibrary(AppConfig config, ILogger<KaraokeLibrary> logger)
    {
        _config = config;
        _logger = logger;
    }

    public SongScanResult? Last { get; private set; }

    /// <summary>Where an UltraStar collection usually lives; used when no song folder is configured.</summary>
    public const string DefaultCollectionFolder = @"D:\KARAOKE\songs";

    /// <summary>Whether the last scan has this song (any folder); null before the first scan.</summary>
    public bool? HasSongNow(string artist, string title) => Last is null ? null : _allKeys.Contains(SongClusters.KeyOf(artist, title));

    private IReadOnlySet<string> _communityKeys = new HashSet<string>();
    private IReadOnlySet<string> _allKeys = new HashSet<string>();
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public IReadOnlyList<string> Folders
    {
        get
        {
            var folders = (_config.KaraokeSongFolders ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            // Nothing configured: an UltraStar collection in the usual place is found by itself.
            if (folders.Count == 0 && OperatingSystem.IsWindows() && Directory.Exists(DefaultCollectionFolder)) folders.Add(DefaultCollectionFolder);
            if (Environment.GetEnvironmentVariable(SongsDirEnvironmentVariable) is { Length: > 0 } dev)
                folders.AddRange(dev.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            folders.Add(IngestFolder);
            return folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
        }
    }

    /// <summary>
    /// Where imported songs go: the configured folder, else "Singularity" next to an existing
    /// D:\KARAOKE collection, else Music\Singularity. Kept apart from the song folders the user
    /// collected, which Singularity never writes to.
    /// </summary>
    public string IngestFolder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_config.KaraokeIngestFolder)) return _config.KaraokeIngestFolder.Trim();
            if (OperatingSystem.IsWindows() && Directory.Exists(@"D:\KARAOKE")) return @"D:\KARAOKE\Singularity";
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Singularity");
        }
    }

    /// <summary>How long an imported song wears its "NEW" badge.</summary>
    public static readonly TimeSpan NewFor = TimeSpan.FromDays(3);

    /// <summary>Raised (on any thread) when songs were added to a song folder, e.g. by an import.</summary>
    public event Action? SongsAdded;

    public void NotifySongsAdded() => SongsAdded?.Invoke();

    /// <summary>An imported song made in the last few days (its metadata.json is that recent).</summary>
    public bool IsNew(SongEntry entry)
    {
        var root = Path.GetFullPath(IngestFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!entry.Folder.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        var metadata = new FileInfo(Path.Combine(entry.Folder, SongPackage.MetadataFileName));
        return metadata.Exists && DateTime.UtcNow - metadata.LastWriteTimeUtc < NewFor;
    }

    /// <summary>The quality tier in the song's metadata.json (charts Singularity made); null when there is none.</summary>
    public QualityTier? TierOf(SongEntry entry)
    {
        var path = Path.Combine(entry.Folder, SongPackage.MetadataFileName);
        if (!File.Exists(path)) return null;
        try
        {
            return SongPackage.Deserialize(File.ReadAllText(path)).Quality.Tier;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The music video that goes with an audio file, for players outside the sing screen: a karaoke song
    /// whose audio is that very file, or a song Singularity made from it (its audio is a copy: same size).
    /// Only then does the song's #VIDEOGAP fit this file exactly. Null when there is none, or before the
    /// first scan (which this starts).
    /// </summary>
    public KaraokeVideo? FindVideo(string? audioPath)
    {
        if (string.IsNullOrEmpty(audioPath)) return null;
        if (Last is not { } scan)
        {
            _ = ScanAsync();
            return null;
        }
        var withVideo = scan.Songs.Where(s => s.VideoPath is not null && s.AudioPath is not null).ToList();
        var song = withVideo.FirstOrDefault(s => string.Equals(Path.GetFullPath(s.AudioPath!), Path.GetFullPath(audioPath), StringComparison.OrdinalIgnoreCase));
        if (song is null)
        {
            long length;
            try { length = new FileInfo(audioPath).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
            var ingest = Path.GetFullPath(IngestFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            song = withVideo.FirstOrDefault(s => s.Folder.StartsWith(ingest, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetExtension(s.AudioPath), Path.GetExtension(audioPath), StringComparison.OrdinalIgnoreCase)
                && SafeLength(s.AudioPath!) == length);
        }
        if (song is null) return null;
        bool synced = !IsUnsyncedPackage(song.Folder);
        return new KaraokeVideo(song.VideoPath!, synced ? song.Song.VideoGapMs : 0, synced);
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return -1; }
    }

    private static bool IsUnsyncedPackage(string folder)
    {
        var path = Path.Combine(folder, SongPackage.MetadataFileName);
        if (!File.Exists(path)) return false;
        try { return !SongPackage.Deserialize(File.ReadAllText(path)).Timing.VideoStructureValid; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException) { return false; }
    }

    /// <summary>
    /// Whether the collection already has this song (same artist and title, as versions are grouped).
    /// <paramref name="includeImported"/> false only counts the user's own song folders, not songs Singularity made.
    /// Scans once if nothing was scanned yet.
    /// </summary>
    public async Task<bool> HasSongAsync(string artist, string title, bool includeImported, CancellationToken ct = default)
    {
        if (Last is null)
        {
            await _scanGate.WaitAsync(ct);
            try
            {
                if (Last is null) await ScanAsync(ct);
            }
            finally
            {
                _scanGate.Release();
            }
        }
        var key = SongClusters.KeyOf(artist, title);
        return (includeImported ? _allKeys : _communityKeys).Contains(key);
    }

    public async Task<SongScanResult> ScanAsync(CancellationToken ct = default)
    {
        var folders = Folders;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await Task.Run(() => SongScanner.Scan(folders, ct), ct).ConfigureAwait(false);
        _logger.LogInformation("Karaoke: scanned {Folders} folder(s): {Songs} songs, {Failures} unreadable, in {Ms} ms",
            folders.Count, result.Songs.Count, result.Failures.Count, sw.ElapsedMilliseconds);
        var ingest = Path.GetFullPath(IngestFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _allKeys = result.Songs.Select(s => SongClusters.KeyOf(s.Song.Artist, s.Song.Title)).ToHashSet();
        _communityKeys = result.Songs.Where(s => !s.Folder.StartsWith(ingest, StringComparison.OrdinalIgnoreCase))
            .Select(s => SongClusters.KeyOf(s.Song.Artist, s.Song.Title)).ToHashSet();
        Last = result;
        return result;
    }
}
