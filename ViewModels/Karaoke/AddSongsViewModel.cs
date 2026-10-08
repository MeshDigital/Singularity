using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Singularity.Views;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Singularity.Configuration;
using Singularity.Contracts.Song;
using Singularity.Services.Karaoke.Ingest;

namespace Singularity.ViewModels.Karaoke;

/// <summary>One song in the import table.</summary>
public sealed class IngestRowViewModel : ReactiveObject
{
    private string _stateText = "";
    private double _progress;
    private bool _showProgress;
    private string? _detail;
    private bool _isFailed;
    private bool _isReady;

    public IngestRowViewModel(string key, string song)
    {
        Key = key;
        Song = song;
    }

    public string Key { get; }
    public string Song { get; }
    public string StateText { get => _stateText; private set => this.RaiseAndSetIfChanged(ref _stateText, value); }
    public double Progress { get => _progress; private set => this.RaiseAndSetIfChanged(ref _progress, value); }
    public bool ShowProgress { get => _showProgress; private set => this.RaiseAndSetIfChanged(ref _showProgress, value); }
    public string? Detail { get => _detail; private set => this.RaiseAndSetIfChanged(ref _detail, value); }
    public bool IsFailed { get => _isFailed; private set => this.RaiseAndSetIfChanged(ref _isFailed, value); }
    public bool IsReady { get => _isReady; private set => this.RaiseAndSetIfChanged(ref _isReady, value); }

    public void Update(IngestItem item)
    {
        StateText = StateTextFor(item);
        ShowProgress = item.State == IngestState.Building;
        Progress = item.Stage is { } stage ? OverallProgress(stage, item.Progress) : 0;
        Detail = item.Detail;
        IsFailed = item.State == IngestState.Failed;
        IsReady = item.State == IngestState.Ready;
    }

    internal static string StateTextFor(IngestItem item) => item.State switch
    {
        IngestState.Waiting => "Queued",
        IngestState.Searching => "Searching Soulseek",
        IngestState.Downloading => "Downloading audio",
        IngestState.InQueue => "Waiting for the AI",
        IngestState.Building => item.Stage switch
        {
            IngestStage.Preparing => "Preparing",
            IngestStage.FindingChart => "Looking for a community chart",
            IngestStage.FetchingLyrics => "Finding lyrics",
            IngestStage.GeneratingChart => "Generating AI chart",
            IngestStage.SeparatingVocals => "Separating the vocals",
            IngestStage.PlacingChart => "Placing the community chart",
            IngestStage.DownloadingVideo => "Downloading video",
            IngestStage.SyncingVideo => "Syncing video",
            _ => "Finishing",
        },
        IngestState.Ready => item.Tier switch
        {
            QualityTier.APlus => "Ready · quality A+",
            QualityTier.A => "Ready · quality A",
            QualityTier.B => "Ready · quality B",
            QualityTier.ReviewRequired => "Ready · needs checking",
            _ => "Ready",
        },
        _ => "Failed",
    };

    /// <summary>The chart is nearly all of the time; the other stages are a sliver each.</summary>
    private static double OverallProgress(IngestStage stage, double fraction) => stage switch
    {
        IngestStage.Preparing => 0.01 * fraction,
        IngestStage.FindingChart => 0.01 + 0.01 * fraction,
        IngestStage.FetchingLyrics => 0.02 + 0.03 * fraction,
        IngestStage.GeneratingChart => 0.05 + 0.80 * fraction,
        IngestStage.SeparatingVocals => 0.05 + 0.75 * fraction,
        IngestStage.PlacingChart => 0.80 + 0.05 * fraction,
        IngestStage.DownloadingVideo => 0.85 + 0.05 * fraction,
        IngestStage.SyncingVideo => 0.90 + 0.07 * fraction,
        _ => 0.97 + 0.03 * fraction,
    };
}

/// <summary>
/// The "Add songs" page: paste a Spotify link (a song, album or playlist) and follow each song from
/// the Soulseek download through the AI chart to the library. An audio file already on disk can be
/// added too.
/// </summary>
public sealed class AddSongsViewModel : ReactiveObject
{
    private readonly KaraokeIngestService _ingest;
    private readonly ILogger<AddSongsViewModel> _logger;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private bool _isCatchingUp;
    private string _link = "";
    private string _message = "";
    private bool _isImporting;
    private bool _refreshPosted;

    public AddSongsViewModel(KaraokeIngestService ingest, AppConfig config, ConfigManager configManager, ILogger<AddSongsViewModel> logger,
        WatchedPlaylistService? watched = null)
    {
        _watched = watched;
        if (watched is not null) watched.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshWatched);
        UnwatchCommand = new RelayCommand<WatchedPlaylistRow>(row => { if (row is not null) _watched?.Unwatch(row.Url); });
        CheckWatchedCommand = new AsyncRelayCommand(async () => { if (_watched is not null) await _watched.CheckAllAsync(); });
        RefreshWatched();
        _config = config;
        _configManager = configManager;
        CatchUpCommand = new AsyncRelayCommand(CatchUpAsync, () => !_isCatchingUp);
        _ingest = ingest;
        _logger = logger;
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsImporting && _ingest.CanImport(Link));
        _ingest.Queue.Changed += PostRefresh;
        Refresh();
    }

    public ObservableCollection<IngestRowViewModel> Rows { get; } = new();

    public string Link
    {
        get => _link;
        set
        {
            this.RaiseAndSetIfChanged(ref _link, value);
            ((AsyncRelayCommand)ImportCommand).RaiseCanExecuteChanged();
            this.RaisePropertyChanged(nameof(DetectedText));
            this.RaisePropertyChanged(nameof(IsPlaylistLink));
        }
    }

    // ── Watched playlists ───────────────────────────────────────────────────
    private readonly WatchedPlaylistService? _watched;
    private bool _watchThis;

    /// <summary>The box holds a Spotify playlist link: it can be watched.</summary>
    public bool IsPlaylistLink => _watched is not null && WatchedPlaylistService.PlaylistId(Link) is not null;

    /// <summary>Keep checking the playlist being added for new songs.</summary>
    public bool WatchThis { get => _watchThis; set => this.RaiseAndSetIfChanged(ref _watchThis, value); }

    public ObservableCollection<WatchedPlaylistRow> Watched { get; } = new();
    public bool HasWatched => Watched.Count > 0;
    public ICommand UnwatchCommand { get; }
    public ICommand CheckWatchedCommand { get; }

    private void RefreshWatched()
    {
        Watched.Clear();
        if (_watched is not null)
            foreach (var w in _watched.Playlists) Watched.Add(new WatchedPlaylistRow(w));
        this.RaisePropertyChanged(nameof(HasWatched));
    }

    /// <summary>What the pasted text is ("Spotify playlist", "12 songs"), shown before adding it.</summary>
    public string DetectedText => _ingest.Describe(Link);

    public string Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isImporting, value);
            ((AsyncRelayCommand)ImportCommand).RaiseCanExecuteChanged();
        }
    }

    public ICommand ImportCommand { get; }

    public string FolderText => $"New songs are saved to {_ingest.IngestFolder()}";

    /// <summary>Every finished download becomes a karaoke song, not only links added here.</summary>
    public bool IngestAllDownloads
    {
        get => _config.KaraokeIngestAllDownloads;
        set
        {
            if (value == _config.KaraokeIngestAllDownloads) return;
            _config.KaraokeIngestAllDownloads = value;
            _ = _configManager.SaveAsync(_config);
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Makes karaoke songs from everything already downloaded that the collection doesn't have.</summary>
    public ICommand CatchUpCommand { get; }

    private async Task CatchUpAsync()
    {
        _isCatchingUp = true;
        ((AsyncRelayCommand)CatchUpCommand).RaiseCanExecuteChanged();
        Message = "Looking through your downloads…";
        try
        {
            var (queued, skipped) = await _ingest.MakeSongsFromDownloadsAsync();
            Message = queued == 0
                ? $"Nothing new: all {skipped} downloaded songs are already karaoke songs."
                : $"Making {queued} karaoke songs from your downloads ({skipped} were already in the collection).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Making karaoke songs from downloads failed");
            Message = "That didn't work: " + ex.Message;
        }
        finally
        {
            _isCatchingUp = false;
            ((AsyncRelayCommand)CatchUpCommand).RaiseCanExecuteChanged();
        }
    }

    public string? OfflineText => RuntimeOptions.Offline
        ? "Offline mode (--offline): Soulseek is not connected, so Spotify songs will wait in the list until you start Singularity normally. Audio files work."
        : null;

    public bool IsOffline => RuntimeOptions.Offline;

    public bool HasRows => Rows.Count > 0;

    public string HeldText => _ingest.Queue.IsHeld ? "Paused while someone sings." : "";

    private async Task ImportAsync()
    {
        IsImporting = true;
        Message = "Reading the link…";
        try
        {
            var link = Link;
            var error = await _ingest.ImportAsync(link);
            Message = error ?? "Added. Each song appears below as it is found and made.";
            if (error is null && WatchThis && _watched?.Watch(link) == true)
                Message += $" The playlist is checked every {WatchedPlaylistService.IntervalMinutes} minutes for songs added on Spotify.";
            if (error is null) Link = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Karaoke import of {Link} failed", Link);
            Message = "The import failed: " + ex.Message;
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>An audio file chosen in the file picker. "Artist - Title" comes from its tags, else its name.</summary>
    public void AddFile(string path)
    {
        var (artist, title) = NameOf(path);
        _ingest.ImportFile(path, artist, title);
        Message = $"Added {artist} - {title}.";
    }

    internal static (string Artist, string Title) NameOf(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tagArtist = file.Tag.FirstPerformer ?? file.Tag.FirstAlbumArtist;
            if (!string.IsNullOrWhiteSpace(tagArtist) && !string.IsNullOrWhiteSpace(file.Tag.Title))
                return (tagArtist.Trim(), file.Tag.Title.Trim());
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException)
        {
            // Fall back to the file name.
        }
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split(" - ", 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("Unknown artist", name);
    }

    private void PostRefresh()
    {
        if (_refreshPosted) return;
        _refreshPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPosted = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        foreach (var item in _ingest.Queue.Items)
        {
            var row = Rows.FirstOrDefault(r => r.Key == item.Key);
            if (row is null)
            {
                row = new IngestRowViewModel(item.Key, $"{item.Artist} - {item.Title}");
                Rows.Insert(0, row); // newest on top
            }
            row.Update(item);
        }
        this.RaisePropertyChanged(nameof(HasRows));
        this.RaisePropertyChanged(nameof(HeldText));
    }
}

/// <summary>A watched playlist on the Add songs page.</summary>
public sealed class WatchedPlaylistRow
{
    public WatchedPlaylistRow(WatchedPlaylist playlist) => Playlist = playlist;

    public WatchedPlaylist Playlist { get; }
    public string Url => Playlist.Url;

    public string Status => Playlist.LastCheckedUtc is { } at
        ? $"{Playlist.LastResult} · checked {at.ToLocalTime():HH:mm}"
        : $"Checked every {WatchedPlaylistService.IntervalMinutes} minutes";
}
