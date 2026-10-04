using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
    private readonly ILogger<KaraokeIngestService> _logger;
    private readonly IDisposable _subscription;
    private readonly string _statePath;
    private readonly object _lock = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>A track asked for as karaoke whose song isn't built yet.</summary>
    private sealed record Pending(string Artist, string Title, List<Guid> JobIds);

    public KaraokeIngestService(
        ImportOrchestrator orchestrator,
        SpotifyImportProvider spotify,
        ILibraryService library,
        IEventBus events,
        KaraokeWorker worker,
        KaraokeLibrary songs,
        ILoggerFactory loggers)
    {
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
            new YtDlpVideoFinder(loggers.CreateLogger<YtDlpVideoFinder>()));
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
                if (_pending.TryGetValue(track.TrackUniqueHash, out var known)) known.JobIds.AddRange(result.JobIds.Except(known.JobIds));
                else _pending[track.TrackUniqueHash] = new Pending(track.Artist, track.Title, result.JobIds.ToList());
            }
            Save();
        }
        foreach (var track in result.Queued)
            Queue.Track(track.TrackUniqueHash, track.Artist, track.Title);
        foreach (var track in result.AlreadyDownloaded)
            EnqueueDownloaded(track);
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
            if (!_pending.TryGetValue(e.TrackGlobalId, out pending) || !pending.JobIds.Contains(e.ProjectId)) return;
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
                    if (await _library.GetPlaylistTrackByHashAsync(e.ProjectId, e.TrackGlobalId) is { } track) EnqueueDownloaded(track);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Karaoke import: couldn't look up the finished download {Hash}", e.TrackGlobalId);
                }
                break;
        }
    }

    private void EnqueueDownloaded(PlaylistTrack track)
    {
        if (string.IsNullOrEmpty(track.ResolvedFilePath) || !File.Exists(track.ResolvedFilePath))
        {
            Queue.Track(track.TrackUniqueHash, track.Artist, track.Title, IngestState.Failed, "The downloaded file is missing.");
            return;
        }
        Queue.Enqueue(track.TrackUniqueHash, SourceFor(track));
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
                if (done is not null) EnqueueDownloaded(done);
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
