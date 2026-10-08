using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Configuration;

namespace Singularity.Services.Karaoke.Ingest;

/// <summary>A watched playlist as the Add songs page shows it.</summary>
/// <param name="LastResult">"3 new songs", "Nothing new", or what went wrong; null before the first check.</param>
public sealed record WatchedPlaylist(string Url, DateTime? LastCheckedUtc, string? LastResult);

/// <summary>
/// Spotify playlists that are checked again now and then, so songs added to them on Spotify become karaoke songs by
/// themselves: a "karaoke" playlist friends add to before a party. Each check is an ordinary karaoke import of the
/// playlist; ORBIT merges it into the playlist it made before and downloads only the new songs, and songs the
/// collection already has are skipped. Checked a few minutes after startup and every <see cref="IntervalMinutes"/>;
/// never in offline mode.
/// </summary>
public sealed class WatchedPlaylistService : IDisposable
{
    public const int IntervalMinutes = 30;
    private static readonly TimeSpan FirstCheckAfter = TimeSpan.FromMinutes(2);

    private readonly KaraokeIngestService _ingest;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly ILogger<WatchedPlaylistService> _logger;
    private readonly Dictionary<string, WatchedPlaylist> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _checking = new(1, 1);
    private Timer? _timer;

    public WatchedPlaylistService(KaraokeIngestService ingest, AppConfig config, ConfigManager configManager, ILogger<WatchedPlaylistService> logger)
    {
        _ingest = ingest;
        _config = config;
        _configManager = configManager;
        _logger = logger;
    }

    /// <summary>Raised when the list or a playlist's last check changes (on any thread).</summary>
    public event Action? Changed;

    public IReadOnlyList<WatchedPlaylist> Playlists
    {
        get
        {
            lock (_state)
                return Urls().Select(u => _state.TryGetValue(u, out var s) ? s : new WatchedPlaylist(u, null, null)).ToList();
        }
    }

    /// <summary>Starts the timer (not in offline mode).</summary>
    public void Start()
    {
        if (RuntimeOptions.Offline || _timer is not null) return;
        _timer = new Timer(_ => _ = CheckAllAsync(), null, FirstCheckAfter, TimeSpan.FromMinutes(IntervalMinutes));
    }

    /// <summary>The playlist's id from any Spotify playlist link (with or without ?si=, or a spotify: URI); null otherwise.</summary>
    public static string? PlaylistId(string link)
    {
        var m = Regex.Match(link ?? "", @"(?:open\.spotify\.com/(?:[a-z-]+/)?playlist/|spotify:playlist:)([A-Za-z0-9]{16,40})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Starts watching a Spotify playlist; false when the link isn't one or is watched already.</summary>
    public bool Watch(string link)
    {
        if (PlaylistId(link) is not { } id) return false;
        var url = $"https://open.spotify.com/playlist/{id}";
        var urls = Urls();
        if (urls.Contains(url, StringComparer.OrdinalIgnoreCase)) return false;
        Save(urls.Append(url));
        lock (_state) _state[url] = new WatchedPlaylist(url, DateTime.UtcNow, "Added; songs added on Spotify come in by themselves");
        _logger.LogInformation("Watched playlists: watching {Url}", url);
        Changed?.Invoke();
        return true;
    }

    public void Unwatch(string url)
    {
        Save(Urls().Where(u => !string.Equals(u, url, StringComparison.OrdinalIgnoreCase)));
        lock (_state) _state.Remove(url);
        _logger.LogInformation("Watched playlists: stopped watching {Url}", url);
        Changed?.Invoke();
    }

    /// <summary>Checks every watched playlist now (one check at a time).</summary>
    public async Task CheckAllAsync()
    {
        if (RuntimeOptions.Offline) return;
        if (!await _checking.WaitAsync(0)) return; // a check is running
        try
        {
            foreach (var url in Urls())
            {
                string result;
                try
                {
                    var error = await _ingest.ImportAsync(url);
                    var (downloading, making) = _ingest.LastImport;
                    int fresh = downloading + making;
                    result = error ?? (fresh == 0 ? "Nothing new" : fresh == 1 ? "1 new song" : $"{fresh} new songs");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "Watched playlists: checking {Url} failed", url);
                    result = "Couldn't check it: " + ex.Message;
                }
                lock (_state) _state[url] = new WatchedPlaylist(url, DateTime.UtcNow, result);
                _logger.LogInformation("Watched playlists: {Url}: {Result}", url, result);
                Changed?.Invoke();
            }
        }
        finally
        {
            _checking.Release();
        }
    }

    private List<string> Urls() => (_config.KaraokeWatchedPlaylists ?? "")
        .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void Save(IEnumerable<string> urls)
    {
        _config.KaraokeWatchedPlaylists = string.Join(" | ", urls);
        _configManager.Save(_config);
    }

    public void Dispose() => _timer?.Dispose();
}
