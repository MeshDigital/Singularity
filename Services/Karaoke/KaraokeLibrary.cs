using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Configuration;
using Singularity.Karaoke.Library;

namespace Singularity.Services.Karaoke;

/// <summary>The karaoke song collection: the configured UltraStar folders, scanned on demand (never written to).</summary>
public sealed class KaraokeLibrary
{
    /// <summary>Extra song folder for development runs, on top of the configured ones.</summary>
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
            if (Environment.GetEnvironmentVariable(SongsDirEnvironmentVariable) is { Length: > 0 } dev) folders.Add(dev);
            return folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
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
