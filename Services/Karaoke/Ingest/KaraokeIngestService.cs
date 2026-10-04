using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Models;
using Singularity.Services.ImportProviders;
using Singularity.Services.InputParsers;
using Singularity.Services.Lyrics;

namespace Singularity.Services.Karaoke.Ingest;

/// <summary>
/// One-click songs from Spotify links. The link goes through ORBIT's own import, so its Soulseek
/// search, ranking and download do the finding; this service only remembers which tracks were
/// asked for as karaoke, follows their download events, and hands each finished download to the
/// <see cref="IngestQueue"/>, which builds the song folder in <see cref="KaraokeLibrary.IngestFolder"/>.
/// What's still on its way is saved, so a restart mid-download picks up where it was.
/// </summary>
public sealed class KaraokeIngestService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly ImportOrchestrator _orchestrator;
    private readonly SpotifyImportProvider _spotify;
    private readonly ILibraryService _library;
    private readonly KaraokeLibrary _songs;
    private readonly Singularity.Configuration.AppConfig _config;

    /// <summary>Songs (by cluster key) queued or made this session, so a second download of one isn't charted twice.</summary>
    private readonly HashSet<string> _made = new(StringComparer.Ordinal);
    private readonly ILogger<KaraokeIngestService> _logger;
    private readonly IDisposable _subscription;
    private readonly string _statePath;
    private readonly object _lock = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// A track asked for as karaoke whose song isn't built yet, with what Spotify said about it at import.
    /// Kept here because ORBIT's database row can lose those details (re-linking a file that was already
    /// downloaded refills it from the library entry), while the song needs them for its metadata.
    /// </summary>
    internal sealed record Pending(string Artist, string Title, List<Guid> JobIds, TrackDetails? Details = null);

    internal sealed record TrackDetails(string? Album, string? SpotifyTrackId, string? Isrc, int? DurationMs, string? CoverUrl, int? Year)
    {
        public static TrackDetails Of(PlaylistTrack t) => new(
            string.IsNullOrWhiteSpace(t.Album) ? null : t.Album,
            string.IsNullOrWhiteSpace(t.SpotifyTrackId) ? null : t.SpotifyTrackId,
            string.IsNullOrWhiteSpace(t.ISRC) ? null : t.ISRC,
            t.CanonicalDuration is > 30_000 and var ms ? ms : null,
            string.IsNullOrWhiteSpace(t.AlbumArtUrl) ? null : t.AlbumArtUrl,
            t.ReleaseDate?.Year);
    }

    public KaraokeIngestService(
        ImportOrchestrator orchestrator,
        SpotifyImportProvider spotify,
        ILibraryService library,
        IEventBus events,
        KaraokeWorker worker,
        KaraokeLibrary songs,
        Singularity.Configuration.AppConfig config,
        ILoggerFactory loggers)
    {
        _songs = songs;
        _config = config;
        _orchestrator = orchestrator;
        _spotify = spotify;
        _library = library;
        _logger = loggers.CreateLogger<KaraokeIngestService>();
        var packager = new KaraokePackager(
            worker,
            new LrclibLyricsLookup(new LrclibClient(new HttpClient(), loggers.CreateLogger<LrclibClient>())),
            new FfmpegIngestMedia(Http),
            KaraokePackager.DefaultStagingRoot,
            loggers.CreateLogger<KaraokePackager>(),
            new YtDlpVideoFinder(loggers.CreateLogger<YtDlpVideoFinder>()),
            new Usdb.UsdbCommunityCharts(new Usdb.UsdbClient(Usdb.UsdbCredentials.Load, loggers.CreateLogger<Usdb.UsdbClient>()),
                loggers.CreateLogger<Usdb.UsdbCommunityCharts>()));
        Queue = new IngestQueue(packager, () => songs.IngestFolder, _logger);
        Queue.Changed += ForgetFinished;
        Queue.PackageReady += _ => songs.NotifySongsAdded();
        IngestFolder = () => songs.IngestFolder;
        _statePath = Path.Combine(Path.GetDirectoryName(KaraokePackager.DefaultStagingRoot)!, "ingest-pending.json");
        _subscription = events.GetEvent<TrackStateChangedEvent>().Subscribe(e => _ = OnTrackStateAsync(e));
        _ = ResumeAsync();
    }

    public IngestQueue Queue { get; }

    /// <summary>Where finished songs go.</summary>
    public Func<string> IngestFolder { get; }

    /// <summary>True for links ORBIT's Spotify import understands (a song, album or playlist).</summary>
    public bool CanImport(string link) => _spotify.CanHandle(link?.Trim() ?? "");

    /// <summary>Imports a Spotify link; returns an error for the user, or null when its songs are on their way.</summary>
    public async Task<string?> ImportAsync(string link)
    {
        link = link.Trim();
        if (!CanImport(link)) return "That isn't a Spotify link.";
        var result = await _orchestrator.SilentImportWithResultAsync(_spotify, link);
        if (result is null) return "Spotify returned no songs for that link.";

        lock (_lock)
        {
            foreach (var track in result.Queued.Concat(result.AlreadyDownloaded).Where(t => t.TrackUniqueHash.Length > 0))
            {
                if (_pending.TryGetValue(track.TrackUniqueHash, out var known))
                {
                    known.JobIds.AddRange(result.JobIds.Except(known.JobIds));
                    if (known.Details is null) _pending[track.TrackUniqueHash] = known with { Details = TrackDetails.Of(track) };
                }
                else _pending[track.TrackUniqueHash] = new Pending(track.Artist, track.Title, result.JobIds.ToList(), TrackDetails.Of(track));
            }
            Save();
        }
        foreach (var track in result.Queued)
            Queue.Track(track.TrackUniqueHash, track.Artist, track.Title);
        foreach (var track in result.AlreadyDownloaded)
            EnqueueDownloaded(track, DetailsFor(track.TrackUniqueHash));
        _logger.LogInformation("Karaoke import of {Link}: {Queued} to download, {Present} already downloaded",
            link, result.Queued.Count, result.AlreadyDownloaded.Count);
        return null;
    }

    /// <summary>Makes a song from an audio file on disk (no download).</summary>
    public void ImportFile(string audioPath, string artist, string title) =>
        Queue.Enqueue("file:" + Path.GetFullPath(audioPath), new IngestSource(audioPath, artist, title));

    private async Task OnTrackStateAsync(TrackStateChangedEvent e)
    {
        Pending? pending;
        lock (_lock)
        {
            if (!_pending.TryGetValue(e.TrackGlobalId, out pending) || !pending.JobIds.Contains(e.ProjectId)) pending = null;
        }
        if (pending is null)
        {
            // Not asked for on the Add songs page: a download from the Import tab, Search, … becomes a
            // karaoke song too (unless switched off), when the collection doesn't have it yet.
            if (e.State == PlaylistTrackState.Completed && _config.KaraokeIngestAllDownloads) await OnOtherDownloadAsync(e);
            return;
        }

        switch (e.State)
        {
            case PlaylistTrackState.Searching or PlaylistTrackState.Queued:
                Queue.Track(e.TrackGlobalId, pending.Artist, pending.Title, IngestState.Searching);
                break;
            case PlaylistTrackState.Downloading or PlaylistTrackState.Converting:
                Queue.Track(e.TrackGlobalId, pending.Artist, pending.Title, IngestState.Downloading);
                break;
            case PlaylistTrackState.WaitingForConnection:
                Queue.Track(e.TrackGlobalId, pending.Artist, pending.Title, IngestState.Waiting, "Waiting for the Soulseek connection.");
                break;
            case PlaylistTrackState.Failed or PlaylistTrackState.Cancelled:
                Queue.Track(e.TrackGlobalId, pending.Artist, pending.Title, IngestState.Failed,
                    e.Error ?? (e.State == PlaylistTrackState.Cancelled ? "The download was cancelled." : "The download failed."));
                break;
            case PlaylistTrackState.Completed:
                try
                {
                    if (await _library.GetPlaylistTrackByHashAsync(e.ProjectId, e.TrackGlobalId) is { } track) EnqueueDownloaded(track, pending.Details);
                    else _logger.LogWarning("Karaoke import: download {Hash} finished but its track isn't in project {Project}", e.TrackGlobalId, e.ProjectId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Karaoke import: couldn't look up the finished download {Hash}", e.TrackGlobalId);
                }
                break;
        }
    }

    private async Task OnOtherDownloadAsync(TrackStateChangedEvent e)
    {
        try
        {
            if (await _library.GetPlaylistTrackByHashAsync(e.ProjectId, e.TrackGlobalId) is { } track)
                await EnqueueIfNewAsync(track, TrackDetails.Of(track), includeImported: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Karaoke import: couldn't take on the download {Hash}", e.TrackGlobalId);
        }
    }

    /// <summary>
    /// Makes karaoke songs from everything already downloaded (e.g. a playlist imported before this was
    /// switched on), skipping songs the collection has. Returns how many were queued and skipped.
    /// </summary>
    public async Task<(int Queued, int Skipped)> MakeSongsFromDownloadsAsync(CancellationToken ct = default)
    {
        int queued = 0, skipped = 0;
        var tracks = (await _library.GetAllPlaylistTracksAsync())
            .Where(t => t.Status == TrackStatus.Downloaded && !string.IsNullOrEmpty(t.ResolvedFilePath) && File.Exists(t.ResolvedFilePath))
            .GroupBy(t => t.TrackUniqueHash.Length > 0 ? t.TrackUniqueHash : t.ResolvedFilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (await EnqueueIfNewAsync(track, TrackDetails.Of(track), includeImported: true, ct)) queued++;
            else skipped++;
        }
        _logger.LogInformation("Karaoke import from downloads: {Queued} queued, {Skipped} already in the collection", queued, skipped);
        return (queued, skipped);
    }

    /// <summary>Queues the download unless the collection (or this session) already has the song.</summary>
    private async Task<bool> EnqueueIfNewAsync(PlaylistTrack track, TrackDetails? details, bool includeImported, CancellationToken ct = default)
    {
        var key = Singularity.Karaoke.Library.SongClusters.KeyOf(track.Artist, track.Title);
        lock (_lock)
        {
            if (_made.Contains(key)) return false;
        }
        if (await _songs.HasSongAsync(track.Artist, track.Title, includeImported, ct))
        {
            _logger.LogDebug("Karaoke import: {Artist} - {Title} is already in the collection", track.Artist, track.Title);
            return false;
        }
        lock (_lock)
        {
            if (!_made.Add(key)) return false;
        }
        EnqueueDownloaded(track, details);
        return true;
    }

    private TrackDetails? DetailsFor(string hash)
    {
        lock (_lock) return _pending.TryGetValue(hash, out var p) ? p.Details : null;
    }

    private void EnqueueDownloaded(PlaylistTrack track, TrackDetails? details)
    {
        if (string.IsNullOrEmpty(track.ResolvedFilePath) || !File.Exists(track.ResolvedFilePath))
        {
            _logger.LogWarning("Karaoke import: {Artist} - {Title} is downloaded but its file isn't there ({Path})", track.Artist, track.Title, track.ResolvedFilePath);
            Queue.Track(track.TrackUniqueHash, track.Artist, track.Title, IngestState.Failed, "The downloaded file is missing.");
            return;
        }
        _logger.LogInformation("Karaoke import: {Artist} - {Title} downloaded, making the song from {Path}", track.Artist, track.Title, track.ResolvedFilePath);
        Queue.Enqueue(track.TrackUniqueHash, SourceFor(track, details));
    }

    /// <summary>The download as the packager needs it: the file from ORBIT's row, the details as Spotify gave them at import.</summary>
    internal static IngestSource SourceFor(PlaylistTrack t, TrackDetails? details)
    {
        var fromRow = SourceFor(t);
        if (details is null) return fromRow;
        return fromRow with
        {
            Album = details.Album ?? fromRow.Album,
            TrackId = details.SpotifyTrackId is { } id ? "spotify:track:" + id : fromRow.TrackId,
            Isrc = details.Isrc ?? fromRow.Isrc,
            ExpectedDurationMs = details.DurationMs ?? fromRow.ExpectedDurationMs,
            CoverUrl = details.CoverUrl ?? fromRow.CoverUrl,
            Year = details.Year ?? fromRow.Year,
        };
    }

    internal static IngestSource SourceFor(PlaylistTrack t) => new(
        t.ResolvedFilePath,
        t.Artist,
        t.Title,
        string.IsNullOrWhiteSpace(t.Album) ? null : t.Album,
        TrackId: t.SpotifyTrackId is { Length: > 0 } id ? "spotify:track:" + id : null,
        Isrc: string.IsNullOrWhiteSpace(t.ISRC) ? null : t.ISRC,
        // Spotify gives milliseconds; a value under 30 000 was backfilled in seconds by a duration probe.
        ExpectedDurationMs: t.CanonicalDuration is > 30_000 and var ms ? ms : null,
        CoverUrl: t.AlbumArtUrl,
        Year: t.ReleaseDate?.Year);

    /// <summary>After a restart: songs whose download finished meanwhile are built; the rest are listed as waiting.</summary>
    private async Task ResumeAsync()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, Pending>>(await File.ReadAllTextAsync(_statePath)) ?? new();
            lock (_lock)
            {
                foreach (var (hash, p) in saved) _pending.TryAdd(hash, p);
            }
            foreach (var (hash, p) in saved)
            {
                PlaylistTrack? done = null;
                foreach (var job in p.JobIds)
                {
                    if (await _library.GetPlaylistTrackByHashAsync(job, hash) is { Status: TrackStatus.Downloaded } t) { done = t; break; }
                }
                if (done is not null) EnqueueDownloaded(done, p.Details);
                else Queue.Track(hash, p.Artist, p.Title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Karaoke import: couldn't resume the saved import list");
        }
    }

    private void ForgetFinished()
    {
        var ready = Queue.Items.Where(i => i.State == IngestState.Ready).Select(i => i.Key).ToList();
        lock (_lock)
        {
            if (ready.Count(_pending.Remove) > 0) Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath + ".tmp", JsonSerializer.Serialize(_pending));
            File.Move(_statePath + ".tmp", _statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Karaoke import: couldn't save the import list");
        }
    }

    public void Dispose() => _subscription.Dispose();
}
