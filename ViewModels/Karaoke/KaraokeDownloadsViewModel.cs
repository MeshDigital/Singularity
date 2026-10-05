using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using Singularity.Services.Karaoke.Ingest;
using Singularity.ViewModels.Downloads;
using Singularity.Views;

namespace Singularity.ViewModels.Karaoke;

/// <summary>One song on its way: the download and, after it, the karaoke step, as one line.</summary>
public sealed class KaraokeDownloadRow : ReactiveObject, IDisposable
{
    private string _statusText = "";
    private double _progress;
    private bool _showProgress;
    private bool _isReady;
    private bool _isFailed;
    private string? _detail;
    private IngestItem? _ingest;

    private readonly Func<string, string, bool?> _inLibrary;

    public KaraokeDownloadRow(DownloadRowViewModel download, Func<string, string, bool?> inLibrary)
    {
        _inLibrary = inLibrary;
        Download = download;
        Download.PropertyChanged += OnDownloadChanged;
        Download.Track.PropertyChanged += OnTrackChanged;
        Refresh();
    }

    public DownloadRowViewModel Download { get; }
    public string Key => Download.Track.GlobalId;
    public string Title => Download.Track.Model.Title is { Length: > 0 } t ? t : Download.Title;
    public string Artist => Download.Track.ArtistName;
    public Bitmap? Cover => Download.Track.ArtworkBitmap;

    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    public double Progress { get => _progress; private set => this.RaiseAndSetIfChanged(ref _progress, value); }
    public bool ShowProgress { get => _showProgress; private set => this.RaiseAndSetIfChanged(ref _showProgress, value); }
    public bool IsReady { get => _isReady; private set => this.RaiseAndSetIfChanged(ref _isReady, value); }
    public bool IsFailed { get => _isFailed; private set => this.RaiseAndSetIfChanged(ref _isFailed, value); }
    public string? Detail { get => _detail; private set => this.RaiseAndSetIfChanged(ref _detail, value); }

    /// <summary>Retry, for a download that failed (the download center's own action).</summary>
    public ICommand? RetryCommand => Download.Status is DownloadRowStatus.Failed or DownloadRowStatus.Cancelled ? Download.PrimaryAction : null;
    public bool CanRetry => RetryCommand is not null && _ingest is null;

    /// <summary>Where the song is: in the karaoke step once its download is done, else in the download.</summary>
    public Stage Phase { get; private set; }

    public enum Stage { Coming, Making, Ready, Failed }

    /// <summary>Raised when <see cref="Phase"/> or <see cref="IsReady"/> changes: the page re-counts and re-filters.</summary>
    public event Action? PhaseChanged;

    /// <summary>Lower-case "title artist", for the page's search.</summary>
    public string SearchKey => $"{Title} {Artist}".ToLowerInvariant();

    public void SetIngest(IngestItem? item)
    {
        _ingest = item;
        Refresh();
    }

    private void OnDownloadChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UnifiedTrackViewModel.ArtworkBitmap) or nameof(UnifiedTrackViewModel.Artwork))
            Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(Cover)));
    }

    private void Refresh()
    {
        var (phaseBefore, readyBefore) = (Phase, IsReady);
        RefreshState();
        if (Phase != phaseBefore || IsReady != readyBefore) PhaseChanged?.Invoke();
    }

    private void RefreshState()
    {
        if (_ingest is { } k && k.State is IngestState.InQueue or IngestState.Building or IngestState.Ready or IngestState.Failed)
        {
            StatusText = k.State == IngestState.Ready ? ReadyText(k) : IngestRowViewModel.StateTextFor(k);
            ShowProgress = k.State == IngestState.Building;
            Progress = k.Progress;
            IsReady = k.State == IngestState.Ready;
            IsFailed = k.State == IngestState.Failed;
            Detail = k.Detail;
            Phase = k.State switch { IngestState.Ready => Stage.Ready, IngestState.Failed => Stage.Failed, _ => Stage.Making };
        }
        else
        {
            var d = Download;
            StatusText = d.Status switch
            {
                DownloadRowStatus.Queued => "Queued",
                DownloadRowStatus.Searching => "Searching Soulseek",
                DownloadRowStatus.Downloading => $"Downloading {d.Progress * 100:0}%",
                DownloadRowStatus.Verifying => "Checking the file",
                DownloadRowStatus.Completed => _inLibrary(Artist, Title) == true ? "Ready to sing" : "Downloaded",
                DownloadRowStatus.Cancelled => "Cancelled",
                _ => "Couldn't be downloaded",
            };
            ShowProgress = d.Status == DownloadRowStatus.Downloading;
            Progress = d.Progress;
            IsReady = d.Status == DownloadRowStatus.Completed && _inLibrary(Artist, Title) == true;
            IsFailed = d.Status is DownloadRowStatus.Failed or DownloadRowStatus.Cancelled;
            Detail = IsFailed && !string.IsNullOrWhiteSpace(d.StatusText) ? d.StatusText : null;
            Phase = IsFailed ? Stage.Failed : d.Status == DownloadRowStatus.Completed ? Stage.Ready : Stage.Coming;
        }
        this.RaisePropertyChanged(nameof(RetryCommand));
        this.RaisePropertyChanged(nameof(CanRetry));
    }

    private static string ReadyText(IngestItem k) => k.Tier switch
    {
        Singularity.Contracts.Song.QualityTier.APlus or Singularity.Contracts.Song.QualityTier.A => "Ready to sing",
        Singularity.Contracts.Song.QualityTier.B => "Ready to sing · chart could be better",
        Singularity.Contracts.Song.QualityTier.ReviewRequired => "Ready · chart needs checking",
        _ => "Ready to sing",
    };

    public void Dispose()
    {
        Download.PropertyChanged -= OnDownloadChanged;
        Download.Track.PropertyChanged -= OnTrackChanged;
    }
}

/// <summary>
/// The Downloads page for karaoke: one line per song, from the Soulseek search through the download to
/// "ready to sing", with counts and the few actions that matter (pause, resume, retry failed). ORBIT's
/// full download center stays one switch away (<see cref="ShowAdvanced"/>).
/// </summary>
public sealed class KaraokeDownloadsViewModel : ReactiveObject
{
    private readonly DownloadCenterViewModel _center;
    private readonly KaraokeIngestService _ingest;
    private readonly Dictionary<DownloadRowViewModel, KaraokeDownloadRow> _byRow = new();
    private bool _showAdvanced;
    private bool _refreshPosted;

    private readonly Singularity.Services.Karaoke.KaraokeLibrary _songs;

    public KaraokeDownloadsViewModel(DownloadCenterViewModel center, KaraokeIngestService ingest, Singularity.Services.Karaoke.KaraokeLibrary songs)
    {
        _center = center;
        _ingest = ingest;
        _songs = songs;
        songs.SongsAdded += PostRefresh;
        if (songs.Last is null) _ = songs.ScanAsync().ContinueWith(_ => PostRefresh(), System.Threading.Tasks.TaskScheduler.Default);
        ((INotifyCollectionChanged)center.HubRows).CollectionChanged += (_, _) => Dispatcher.UIThread.Post(Sync);
        ingest.Queue.Changed += PostRefresh;
        Sync();
    }

    /// <summary>Every song in the download center, newest first.</summary>
    public ObservableCollection<KaraokeDownloadRow> Rows { get; } = new();

    /// <summary>
    /// What the page lists: <see cref="Rows"/> narrowed to the chosen card (<see cref="Filter"/>) and the search,
    /// with songs still moving first (becoming karaoke, then on their way), then failed, then done; newest first
    /// within each.
    /// </summary>
    public ObservableCollection<KaraokeDownloadRow> Shown { get; } = new();

    public enum Show { All, Coming, Making, Ready, Failed }

    private Show _filter = Show.All;
    private string _search = "";

    /// <summary>The card picked above the list; picking it again shows everything.</summary>
    public Show Filter
    {
        get => _filter;
        set
        {
            this.RaiseAndSetIfChanged(ref _filter, value);
            foreach (var name in new[] { nameof(IsAll), nameof(IsComing), nameof(IsMaking), nameof(IsReadyFilter), nameof(IsFailedFilter) })
                this.RaisePropertyChanged(name);
            ApplyFilter();
        }
    }

    public bool IsAll => _filter == Show.All;
    public bool IsComing => _filter == Show.Coming;
    public bool IsMaking => _filter == Show.Making;
    public bool IsReadyFilter => _filter == Show.Ready;
    public bool IsFailedFilter => _filter == Show.Failed;

    public string SearchText
    {
        get => _search;
        set
        {
            this.RaiseAndSetIfChanged(ref _search, value ?? "");
            ApplyFilter();
        }
    }

    /// <summary>Picks a card ("Coming", "Making", "Ready", "Failed", "All"); the picked card again goes back to all.</summary>
    public ICommand ShowCommand => _showCommand ??= new RelayCommand<string>(name =>
    {
        var picked = Enum.TryParse<Show>(name, out var f) ? f : Show.All;
        Filter = picked == _filter ? Show.All : picked;
    });
    private ICommand? _showCommand;

    public string ShownText => Shown.Count == Rows.Count ? $"{Rows.Count} songs" : $"Showing {Shown.Count} of {Rows.Count}";
    public bool HasShown => Shown.Count > 0;

    /// <summary>The filter or search hides everything (but there are songs).</summary>
    public bool NothingMatches => Rows.Count > 0 && Shown.Count == 0;

    private bool Matches(KaraokeDownloadRow r) => _filter switch
    {
        Show.Coming => r.Phase == KaraokeDownloadRow.Stage.Coming,
        Show.Making => r.Phase == KaraokeDownloadRow.Stage.Making,
        Show.Ready => r.IsReady,
        Show.Failed => r.Phase == KaraokeDownloadRow.Stage.Failed,
        _ => true,
    };

    private static int Order(KaraokeDownloadRow r) => r.Phase switch
    {
        KaraokeDownloadRow.Stage.Making => 0,
        KaraokeDownloadRow.Stage.Coming => 1,
        KaraokeDownloadRow.Stage.Failed => 2,
        _ => 3,
    };

    private void ApplyFilter()
    {
        var words = _search.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wanted = Rows.Select((r, i) => (Row: r, Index: i))
            .Where(x => Matches(x.Row) && words.All(x.Row.SearchKey.Contains))
            .OrderBy(x => Order(x.Row)).ThenBy(x => x.Index)
            .Select(x => x.Row).ToList();
        if (!wanted.SequenceEqual(Shown))
        {
            Shown.Clear();
            foreach (var r in wanted) Shown.Add(r);
        }
        foreach (var name in new[] { nameof(ShownText), nameof(HasShown), nameof(NothingMatches) })
            this.RaisePropertyChanged(name);
    }

    public ICommand PauseAllCommand => _center.PauseAllCommand;
    public ICommand ResumeAllCommand => _center.ResumeAllCommand;
    public ICommand RetryFailedCommand => _center.RetryAllFailedCommand;
    public ICommand ClearFinishedCommand => _center.ClearCompletedCommand;

    public int ComingCount => Rows.Count(r => r.Phase == KaraokeDownloadRow.Stage.Coming);
    public int MakingCount => Rows.Count(r => r.Phase == KaraokeDownloadRow.Stage.Making);
    /// <summary>Songs ready to sing (a plain download that isn't a karaoke song yet doesn't count).</summary>
    public int ReadyCount => Rows.Count(r => r.IsReady);
    public int FailedCount => Rows.Count(r => r.Phase == KaraokeDownloadRow.Stage.Failed);
    public bool HasRows => Rows.Count > 0;
    public bool HasFailed => FailedCount > 0;

    /// <summary>Shows ORBIT's full download center (peers, priorities, profiles) instead.</summary>
    public bool ShowAdvanced { get => _showAdvanced; set => this.RaiseAndSetIfChanged(ref _showAdvanced, value); }

    private void Sync()
    {
        var current = _center.HubRows.ToList();
        foreach (var gone in _byRow.Keys.Except(current).ToList())
        {
            var row = _byRow[gone];
            _byRow.Remove(gone);
            Rows.Remove(row);
            row.PhaseChanged -= PostRefresh;
            row.Dispose();
        }
        // Newest first, as songs are added.
        foreach (var d in current.Where(d => !_byRow.ContainsKey(d)))
        {
            var row = new KaraokeDownloadRow(d, _songs.HasSongNow);
            row.PhaseChanged += PostRefresh;
            _byRow[d] = row;
            Rows.Insert(0, row);
        }
        Refresh();
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
        var items = _ingest.Queue.Items.ToDictionary(i => i.Key, StringComparer.Ordinal);
        foreach (var row in Rows) row.SetIngest(items.GetValueOrDefault(row.Key));
        foreach (var name in new[] { nameof(ComingCount), nameof(MakingCount), nameof(ReadyCount), nameof(FailedCount), nameof(HasRows), nameof(HasFailed) })
            this.RaisePropertyChanged(name);
        ApplyFilter();
    }
}
