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

    public IReadOnlyList<string> Folders
    {
        get
        {
            var folders = (_config.KaraokeSongFolders ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
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

    public async Task<SongScanResult> ScanAsync(CancellationToken ct = default)
    {
        var folders = Folders;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await Task.Run(() => SongScanner.Scan(folders, ct), ct).ConfigureAwait(false);
        _logger.LogInformation("Karaoke: scanned {Folders} folder(s): {Songs} songs, {Failures} unreadable, in {Ms} ms",
            folders.Count, result.Songs.Count, result.Failures.Count, sw.ElapsedMilliseconds);
        Last = result;
        return result;
    }
}
