using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Singularity.Karaoke.Library;
using Singularity.Services;
using Singularity.Services.Karaoke;
using Singularity.Views;

namespace Singularity.ViewModels.Karaoke;

/// <summary>One song in the song-select list. The cover is decoded lazily, off the UI thread, when first shown.</summary>
public sealed class SongCardViewModel : ReactiveObject
{
    private bool _coverRequested;

    public SongCardViewModel(SongEntry entry)
    {
        Entry = entry;
        SearchKey = $"{entry.Song.Artist} {entry.Song.Title}".ToLowerInvariant();
    }

    private bool _hasStems;
    private string? _separating;

    public SongEntry Entry { get; }

    /// <summary>Vocals and instrumental are separated, so the original vocals can be turned off while singing.</summary>
    public bool HasStems { get => _hasStems; set { this.RaiseAndSetIfChanged(ref _hasStems, value); this.RaisePropertyChanged(nameof(CanSeparate)); } }

    /// <summary>Progress text while the vocals are being removed; null otherwise.</summary>
    public string? Separating
    {
        get => _separating;
        set
        {
            this.RaiseAndSetIfChanged(ref _separating, value);
            this.RaisePropertyChanged(nameof(IsSeparating));
            this.RaisePropertyChanged(nameof(CanSeparate));
        }
    }

    /// <summary>Imported in the last few days.</summary>
    public bool IsNew { get; init; }

    public bool IsSeparating => _separating is not null;
    public bool CanSeparate => !_hasStems && _separating is null && Entry.IsPlayable;
    public string Title => Entry.Song.Title;
    public string Artist => Entry.Song.Artist;
    public string SearchKey { get; }

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Entry.Song.IsDuet) parts.Add("Duet");
            if (Entry.Song.Year is { } y) parts.Add(y.ToString());
            if (!string.IsNullOrEmpty(Entry.Song.Language)) parts.Add(Entry.Song.Language!);
            if (Entry.VideoPath is not null) parts.Add("Video");
            if (!Entry.IsPlayable) parts.Add("No audio");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Null until decoded; the binding picks it up through PropertyChanged.</summary>
    public Bitmap? Cover
    {
        get
        {
            if (Entry.CoverPath is not { } path) return null;
            if (CoverCache.TryGet(path) is { } cached) return cached;
            if (!_coverRequested)
            {
                _coverRequested = true;
                _ = LoadCoverAsync(path);
            }
            return null;
        }
    }

    private async Task LoadCoverAsync(string path)
    {
        if (await CoverCache.LoadAsync(path).ConfigureAwait(false) is null) return;
        await Dispatcher.UIThread.InvokeAsync(() => this.RaisePropertyChanged(nameof(Cover)));
    }
}

/// <summary>
/// Decoded cover thumbnails (160 px wide), least recently used first out. Bounded so scrolling
/// through thousands of songs doesn't keep thousands of bitmaps alive; an evicted cover that's still
/// on screen stays alive through its Image control and is simply decoded again next time.
/// </summary>
internal static class CoverCache
{
    public const int DecodeWidth = 160;
    public const int Capacity = 300;

    private static readonly Dictionary<string, LinkedListNode<(string Path, Bitmap Bitmap)>> Map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<(string Path, Bitmap Bitmap)> Order = new();
    private static readonly object Lock = new();

    public static Bitmap? TryGet(string path)
    {
        lock (Lock)
        {
            if (!Map.TryGetValue(path, out var node)) return null;
            Order.Remove(node);
            Order.AddFirst(node);
            return node.Value.Bitmap;
        }
    }

    public static async Task<Bitmap?> LoadAsync(string path)
    {
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.MediumQuality);
            }).ConfigureAwait(false);
            lock (Lock)
            {
                if (Map.TryGetValue(path, out var existing)) return existing.Value.Bitmap;
                Map[path] = Order.AddFirst((path, bitmap));
                while (Order.Count > Capacity)
                {
                    Map.Remove(Order.Last!.Value.Path);
                    Order.RemoveLast();
                }
            }
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Serilog.Log.Debug(ex, "Cover {Path} could not be decoded", path);
            return null;
        }
    }
}

/// <summary>Song select: the karaoke collection, searchable, one click to sing.</summary>
public sealed class SongSelectViewModel : ReactiveObject
{
    private readonly KaraokeLibrary _library;
    private readonly SingViewModel _sing;
    private readonly SongPreviewPlayer _preview;
    private readonly StageScreenService _stage;
    private readonly StemStore _stems;
    private readonly StemSeparationService _separation;
    private readonly StemBatchQueue _batch;
    private int _lastBatchFinished = -1;
    private SongCardViewModel? _selectedSong;
    private readonly INavigationService _navigation;
    private readonly ILogger<SongSelectViewModel> _logger;
    private List<SongCardViewModel> _all = new();
    private string _searchText = "";
    private string _statusText = "";
    private bool _isLoading;
    private bool _loaded;

    public SongSelectViewModel(KaraokeLibrary library, SingViewModel sing, SongPreviewPlayer preview, StageScreenService stage,
        StemStore stems, StemSeparationService separation, StemBatchQueue batch, INavigationService navigation, ILogger<SongSelectViewModel> logger)
    {
        _batch = batch;
        _batch.Changed += status => Dispatcher.UIThread.Post(() => OnBatchChanged(status));
        StartBatchCommand = new RelayCommand(StartBatch, () => !_batch.Status.IsRunning);
        PauseBatchCommand = new RelayCommand(() => _batch.SetPaused(!_batch.Status.IsPaused));
        StopBatchCommand = new RelayCommand(_batch.Stop);
        _stage = stage;
        _stems = stems;
        _separation = separation;
        SeparateCommand = new RelayCommand<SongCardViewModel>(card => _ = SeparateAsync(card), card => card?.CanSeparate == true);
        _preview = preview;
        _library = library;
        _sing = sing;
        _navigation = navigation;
        _logger = logger;
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        // An import finished: rescan, if the list was loaded already (otherwise the first visit scans).
        _library.SongsAdded += () => Dispatcher.UIThread.Post(() =>
        {
            if (_loaded && !IsLoading) _ = LoadAsync();
        });
        SingCommand = new RelayCommand<SongCardViewModel>(Sing, card => card?.Entry.IsPlayable == true);
    }

    public ObservableCollection<SongCardViewModel> Songs { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            ApplyFilter();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    /// <summary>The highlighted song; highlighting starts its preview.</summary>
    public SongCardViewModel? SelectedSong
    {
        get => _selectedSong;
        set
        {
            if (value == _selectedSong) return;
            this.RaiseAndSetIfChanged(ref _selectedSong, value);
            _preview.Preview(value?.Entry);
        }
    }

    /// <summary>Called when the page is hidden.</summary>
    public void StopPreview() => _preview.Stop();

    public ICommand SeparateCommand { get; }

    // ── Remove vocals for every song ───────────────────────────────────────
    private string _batchText = "";

    public ICommand StartBatchCommand { get; }
    public ICommand PauseBatchCommand { get; }
    public ICommand StopBatchCommand { get; }
    public bool IsBatchRunning => _batch.Status.IsRunning;
    public string PauseBatchText => _batch.Status.IsPaused ? "Resume" : "Pause";
    public string BatchText { get => _batchText; private set => this.RaiseAndSetIfChanged(ref _batchText, value); }

    private void StartBatch()
    {
        if (!_separation.IsAvailable)
        {
            StatusText = "Removing vocals needs the AI worker (inference\\.venv), which isn't installed.";
            return;
        }
        int todo = _all.Count(c => !c.HasStems && c.Entry.IsPlayable);
        if (todo == 0)
        {
            BatchText = "Every song already has its vocals removable.";
            return;
        }
        // Songs that already have stems are skipped quickly, so pass everything: the counts stay honest.
        _batch.Start(_all.Select(c => c.Entry));
    }

    private void OnBatchChanged(StemBatchStatus s)
    {
        if (s.Finished != _lastBatchFinished)
        {
            _lastBatchFinished = s.Finished;
            foreach (var card in _all.Where(c => !c.HasStems))
                card.HasStems = _stems.Find(card.Entry) is not null;
        }

        string eta = s.Remaining is { } r ? $" · about {(r.TotalHours >= 1 ? $"{(int)r.TotalHours} h {r.Minutes} min" : $"{Math.Max(1, (int)r.TotalMinutes)} min")} left" : "";
        string state = s.IsHeld ? " · waiting while someone sings" : s.IsPaused ? " · paused" : "";
        string failed = s.Failed > 0 ? $" · {s.Failed} failed (see {System.IO.Path.GetFileName(_batch.ErrorLogPath)})" : "";
        BatchText = s.IsRunning
            ? $"Removing vocals: {s.Finished} of {s.Total}{failed}{eta}{state}" + (s.Current is { } c && !s.IsHeld && !s.IsPaused ? $" · now: {c}" : "")
            : s.Total == 0 ? "" : $"Vocals removed: {s.Done} new, {s.Skipped} already done{failed}.";
        this.RaisePropertyChanged(nameof(IsBatchRunning));
        this.RaisePropertyChanged(nameof(PauseBatchText));
        (StartBatchCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ── Where and how big ──────────────────────────────────────────────────
    public IReadOnlyList<StageScreenOption> StageScreens { get; private set; } = Array.Empty<StageScreenOption>();

    /// <summary>Where songs are sung: this window, or full screen on another display.</summary>
    public StageScreenOption? SelectedStageScreen
    {
        get => StageScreens.FirstOrDefault(o => o.Key == _stage.SelectedKey) ?? StageScreens.FirstOrDefault();
        set
        {
            if (value is null || value.Key == _stage.SelectedKey) return;
            _stage.SelectedKey = value.Key;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Size of the stage's text and notes (also adjustable while singing with + and -).</summary>
    public double TextScale
    {
        get => _sing.TextScale;
        set
        {
            _sing.TextScale = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(TextScaleText));
        }
    }

    public string TextScaleText => $"{TextScale:P0}";

    /// <summary>Refreshes the display list (projectors come and go).</summary>
    public void RefreshScreens()
    {
        StageScreens = _stage.Options();
        this.RaisePropertyChanged(nameof(StageScreens));
        this.RaisePropertyChanged(nameof(SelectedStageScreen));
    }

    private async Task SeparateAsync(SongCardViewModel? card)
    {
        if (card is null || !card.CanSeparate) return;
        if (!_separation.IsAvailable)
        {
            StatusText = "Removing vocals needs the AI worker (inference\\.venv), which isn't installed.";
            return;
        }
        card.Separating = "Removing vocals…";
        try
        {
            var progress = new Progress<double>(p => card.Separating = $"Removing vocals… {p:P0}");
            await _separation.SeparateAsync(card.Entry, progress);
            card.HasStems = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Removing vocals failed for {Song}", card.Entry.TxtPath);
            StatusText = $"Removing vocals failed for {card.Title}: {ex.Message}";
        }
        finally
        {
            card.Separating = null;
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand SingCommand { get; }

    /// <summary>Scans once, the first time the page is shown.</summary>
    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : LoadAsync();

    public async Task LoadAsync()
    {
        _loaded = true;
        if (_library.Folders.Count == 0)
        {
            StatusText = $"No song folders. Add UltraStar folders under [Karaoke] SongFolders in config.ini, or set {KaraokeLibrary.SongsDirEnvironmentVariable}.";
            return;
        }

        IsLoading = true;
        StatusText = "Scanning songs…";
        try
        {
            var result = await _library.ScanAsync();
            _all = result.Songs.Select(s => new SongCardViewModel(s) { HasStems = _stems.Find(s) is not null, IsNew = _library.IsNew(s) }).ToList();
            ApplyFilter();
            StatusText = $"{result.Songs.Count} songs" + (result.Failures.Count > 0 ? $" · {result.Failures.Count} unreadable" : "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scanning karaoke songs failed");
            StatusText = "Scanning songs failed: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var words = SearchText.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Songs.Clear();
        foreach (var card in _all.Where(c => words.All(c.SearchKey.Contains)))
            Songs.Add(card);
    }

    private void Sing(SongCardViewModel? card)
    {
        if (card is null || !card.Entry.IsPlayable) return;
        _preview.Stop();
        _sing.Start(card.Entry);
        _navigation.NavigateTo("Sing");
    }
}
