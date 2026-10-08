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

/// <summary>
/// One song in the song-select list, with its versions (community and AI charts, solo and duet) to
/// choose from; everything shown is the chosen version's. The cover is decoded lazily, off the UI
/// thread, when first shown.
/// </summary>
public sealed class SongCardViewModel : ReactiveObject
{
    private readonly Func<SongEntry, bool> _hasStemsFor;
    private readonly Func<SongEntry, bool> _isNewFor;
    private bool _coverRequested;
    private int _versionIndex;

    public SongCardViewModel(SongCluster cluster, Func<SongEntry, bool> hasStems, Func<SongEntry, bool> isNew,
        Func<SongEntry, Singularity.Contracts.Song.ChartCheck?>? checkOf = null)
    {
        _checkOf = checkOf;
        Cluster = cluster;
        _hasStemsFor = hasStems;
        _isNewFor = isNew;
        SearchKey = string.Join(" ", cluster.Versions.Select(v => $"{v.Entry.Song.Artist} {v.Entry.Song.Title}").Distinct()).ToLowerInvariant();
        _hasStems = hasStems(Entry);
    }

    /// <summary>A single chart, for callers that have no cluster.</summary>
    public SongCardViewModel(SongEntry entry)
        : this(new SongCluster("", new[] { new SongVersion(entry, false, null) }), _ => false, _ => false)
    {
    }

    private bool _hasStems;
    private readonly Func<SongEntry, Singularity.Contracts.Song.ChartCheck?>? _checkOf;
    private readonly Dictionary<SongEntry, string?> _warnings = new();

    /// <summary>A line or more that probably doesn't match the singer: from this many on, a card says so.</summary>
    public const int LinesWorthAWarning = 5;

    /// <summary>
    /// Why this version's chart may be wrong, from the check against the original singer when Singularity made it:
    /// "Chart doesn't match the recording", or "7 lines may be off"; null when it's fine or wasn't checked.
    /// </summary>
    public string? ChartWarning
    {
        get
        {
            if (_checkOf is null) return null;
            if (!_warnings.TryGetValue(Entry, out var warning))
            {
                warning = _checkOf(Entry) switch
                {
                    { Mismatch: true } => "Chart doesn't match the recording",
                    { LinesToCheck.Count: >= LinesWorthAWarning } c => $"{c.LinesToCheck.Count} lines may be off",
                    _ => null,
                };
                _warnings[Entry] = warning;
            }
            return warning;
        }
    }

    public bool HasChartWarning => ChartWarning is not null;
    private string? _separating;

    public SongCluster Cluster { get; }
    public SongVersion Version => Cluster.Versions[_versionIndex];
    public SongEntry Entry => Version.Entry;
    public bool HasVersions => Cluster.Versions.Count > 1;

    /// <summary>E.g. "2 of 3 · Duet · Community chart"; just the label when there is one version.</summary>
    public string VersionText => HasVersions ? $"{_versionIndex + 1} of {Cluster.Versions.Count} · {Version.Label}" : Version.Label;

    /// <summary>Moves to the next (+1) or previous (-1) version, wrapping around.</summary>
    public void StepVersion(int step)
    {
        if (!HasVersions) return;
        _versionIndex = ((_versionIndex + step) % Cluster.Versions.Count + Cluster.Versions.Count) % Cluster.Versions.Count;
        _coverRequested = false;
        _hasStems = _hasStemsFor(Entry);
        foreach (var name in new[] { nameof(Version), nameof(Entry), nameof(VersionText), nameof(Details), nameof(Cover), nameof(HasStems), nameof(CanSeparate), nameof(IsNew), nameof(Artist), nameof(ChartWarning), nameof(HasChartWarning) })
            this.RaisePropertyChanged(name);
    }

    /// <summary>Re-checks the shown version's stems (after background separation).</summary>
    public void RefreshStems() => HasStems = _hasStemsFor(Entry);

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

    private DateTime? _addedUtc;

    /// <summary>When the song's folder appeared (newest of its versions), for "Recently added".</summary>
    public DateTime AddedUtc => _addedUtc ??= Cluster.Versions.Select(v =>
    {
        try { return Directory.GetCreationTimeUtc(v.Entry.Folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }).Max();

    /// <summary>The song's best score at any difficulty; null when it was never sung.</summary>
    public int? BestScore { get; init; }

    /// <summary>The shown version was imported in the last few days.</summary>
    public bool IsNew => _isNewFor(Entry);

    public bool IsSeparating => _separating is not null;
    public bool CanSeparate => !_hasStems && _separating is null && Entry.IsPlayable;
    public string Title => Cluster.Title;
    public string Artist => Entry.Song.Artist;
    public string SearchKey { get; }

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (BestScore is { } best) parts.Add($"Best {best:N0}");
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
    private readonly Singularity.Configuration.AppConfig _config;
    private readonly HighScoreStore? _highScores;
    private readonly Services.Karaoke.Ingest.KaraokeIngestService? _ingest;
    private List<SongCardViewModel> _all = new();

    /// <summary>Every song card (all versions grouped), as last scanned: for the phones' search. Replaced, never changed.</summary>
    public IReadOnlyList<SongCardViewModel> AllSongs => _all;
    private string _searchText = "";
    private string _statusText = "";
    private bool _isLoading;
    private bool _loaded;

    public SongSelectViewModel(KaraokeLibrary library, SingViewModel sing, SongPreviewPlayer preview, StageScreenService stage,
        StemStore stems, StemSeparationService separation, StemBatchQueue batch, INavigationService navigation, ILogger<SongSelectViewModel> logger,
        Singularity.Configuration.AppConfig config, HighScoreStore? highScores = null,
        Services.Karaoke.Ingest.KaraokeIngestService? ingest = null, Singularity.Karaoke.Party.PartyQueue? party = null)
    {
        _ingest = ingest;
        Party = party ?? new Singularity.Karaoke.Party.PartyQueue();
        Party.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshQueue);
        AddToQueueCommand = new RelayCommand<SongCardViewModel>(AddToQueue, card => card?.Entry.IsPlayable == true);
        SingNextCommand = new RelayCommand(SingNext);
        RemoveQueuedCommand = new RelayCommand<QueuedSongViewModel>(q => { if (q is not null) Party.Remove(q.Item.Id); });
        MoveUpQueuedCommand = new RelayCommand<QueuedSongViewModel>(q => { if (q is not null) Party.MoveUp(q.Item.Id); });
        RefreshQueue();
        _config = config;
        _highScores = highScores;
        // A new high score shows on the song's card the next time the list is loaded.
        if (highScores is not null) highScores.Changed += () => Dispatcher.UIThread.Post(() => _loaded = false);
        PreviewClock = () => _preview.PositionMs;
        preview.Changed += () => Dispatcher.UIThread.Post(RaisePreview);
        NextVersionCommand = new RelayCommand<SongCardViewModel>(card => StepVersion(card, +1), card => card?.HasVersions == true);
        PreviousVersionCommand = new RelayCommand<SongCardViewModel>(card => StepVersion(card, -1), card => card?.HasVersions == true);
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
        MakeAiChartCommand = new RelayCommand<SongCardViewModel>(card => Rechart(card, preferAi: true), card => card?.Entry.IsPlayable == true);
        // An AI chart can look for a community one; so can any song Singularity made (its community chart is
        // fetched and placed again, with the pitch check), but not a song from the user's own folders.
        FindCommunityChartCommand = new RelayCommand<SongCardViewModel>(card => Rechart(card, preferAi: false),
            card => card?.Version.IsAi == true
                || card?.Entry is { IsPlayable: true } e && File.Exists(Path.Combine(e.Folder, Singularity.Contracts.Song.SongPackage.MetadataFileName)));
        PlayCommand = new RelayCommand<SongCardViewModel>(card => { if (card?.Entry.IsPlayable == true) Jukebox(card.Entry); });
        JukeboxCommand = new RelayCommand(() => { if (RandomSong() is { } first) Jukebox(first); });
        _sing.NextJukeboxSong = RandomSong;
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

    // ── Preview video ──────────────────────────────────────────────────────
    /// <summary>The highlighted song's music video while its preview plays; null otherwise (the cover shows).</summary>
    public string? PreviewVideoPath => _preview.Playing is { } e && e == SelectedSong?.Entry ? e.VideoPath : null;

    /// <summary>#VIDEOGAP of the previewed song (0 for a backdrop video, which isn't synced).</summary>
    public double PreviewVideoGapMs => _preview.Playing?.Song.VideoGapMs ?? 0;

    public bool HasPreviewVideo => PreviewVideoPath is not null;

    /// <summary>Where the preview is in its song, for the video surface.</summary>
    public Func<double?> PreviewClock { get; }

    private void RaisePreview()
    {
        this.RaisePropertyChanged(nameof(PreviewVideoPath));
        this.RaisePropertyChanged(nameof(PreviewVideoGapMs));
        this.RaisePropertyChanged(nameof(HasPreviewVideo));
    }

    public ICommand NextVersionCommand { get; }
    public ICommand PreviousVersionCommand { get; }

    /// <summary>Shows another version of a song; the preview follows when it's the highlighted one.</summary>
    public void StepVersion(SongCardViewModel? card, int step)
    {
        if (card is null || !card.HasVersions) return;
        card.StepVersion(step);
        if (card == SelectedSong) _preview.Preview(card.Entry);
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
        var entries = _all.SelectMany(c => c.Cluster.Versions.Select(v => v.Entry)).ToList();
        int todo = entries.Count(e => e.IsPlayable && _stems.Find(e) is null);
        if (todo == 0)
        {
            BatchText = "Every song already has its vocals removable.";
            return;
        }
        // Songs that already have stems are skipped quickly, so pass everything: the counts stay honest.
        _batch.Start(entries);
    }

    private void OnBatchChanged(StemBatchStatus s)
    {
        if (s.Finished != _lastBatchFinished)
        {
            _lastBatchFinished = s.Finished;
            foreach (var card in _all.Where(c => !c.HasStems))
                card.RefreshStems();
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
            ShowOnStage();
        }
    }

    public string[] Difficulties { get; } = { "Easy", "Medium", "Hard" };

    /// <summary>How close singing must be to count: Easy 2 semitones, Medium 1, Hard exact.</summary>
    public string Difficulty
    {
        get => _sing.Difficulty.ToString();
        set
        {
            if (Enum.TryParse<Singularity.Karaoke.Scoring.Difficulty>(value, out var d)) _sing.Difficulty = d;
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

    private StageBrowseViewModel? _browse;

    /// <summary>Song select as the projector shows it.</summary>
    public StageBrowseViewModel Browse => _browse ??= new StageBrowseViewModel(this);

    private Singularity.Services.Karaoke.Party.PhoneRemoteServer? _phones;

    /// <summary>The phone server, once it exists (it registers itself): the projector shows its QR code.</summary>
    public Singularity.Services.Karaoke.Party.PhoneRemoteServer? Phones
    {
        get => _phones;
        set => this.RaiseAndSetIfChanged(ref _phones, value);
    }

    /// <summary>Puts song select on the stage display, when one is chosen (and takes it down otherwise).</summary>
    public void ShowOnStage() => _stage.ShowIdle(_stage.SelectedKey == StageScreenService.MainWindowKey ? null : Browse);

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

    public ICommand MakeAiChartCommand { get; }
    public ICommand FindCommunityChartCommand { get; }

    private void Rechart(SongCardViewModel? card, bool preferAi)
    {
        if (card?.Entry is not { IsPlayable: true } entry || _ingest is null) return;
        _ingest.Rechart(entry, preferAi);
        StatusText = preferAi
            ? $"Making an AI chart for {card.Title}; follow it on Add songs."
            : $"Looking for a community chart for {card.Title}; follow it on Add songs.";
    }

    /// <summary>Plays a song in the jukebox (no singing), then carries on with random songs from the list.</summary>
    public ICommand PlayCommand { get; }

    /// <summary>The jukebox, shuffling the songs in the list (as searched).</summary>
    public ICommand JukeboxCommand { get; }

    private void Jukebox(SongEntry entry)
    {
        _preview.Stop();
        _sing.StartJukebox(entry);
        _navigation.NavigateTo("Sing");
    }

    private SongEntry? RandomSong()
    {
        var playable = Songs.Where(c => c.Entry.IsPlayable).ToList();
        return playable.Count == 0 ? null : playable[Random.Shared.Next(playable.Count)].Entry;
    }

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
            // One card per song; charts of the same song are its versions.
            var clusters = await Task.Run(() => SongClusters.Build(result.Songs, _library.TierOf, twoPlayers: _config.KaraokeMic2Enabled));
            _all = clusters.Select(c => new SongCardViewModel(c, e => _stems.Find(e) is not null, _library.IsNew, _library.CheckOf)
            {
                BestScore = _highScores?.Best(Singularity.Karaoke.Scoring.HighScoreTable.SongKey(c.Artist, c.Title))?.Score,
            }).ToList();
            RefreshLanguages();
            ApplyFilter();
            int extra = result.Songs.Count - clusters.Count;
            StatusText = (clusters.Count == 1 ? "1 song" : $"{clusters.Count} songs") + (extra > 0 ? $" · {extra} more {(extra == 1 ? "version" : "versions")}" : "")
                         + (result.Failures.Count > 0 ? $" · {result.Failures.Count} unreadable" : "");
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

    // ── Sort and filter ────────────────────────────────────────────────────
    public string[] SortOptions { get; } = { "Artist", "Title", "Recently added", "Year", "Best score" };
    public string[] ShowOptions { get; } = { "All songs", "New", "With video", "Duets", "Community charts", "AI charts", "Not sung yet", "Charts to check" };
    public const string AnyLanguage = "Any language";

    private string _sort = "Artist";
    private string _show = "All songs";
    private string _language = AnyLanguage;

    public string Sort { get => _sort; set { this.RaiseAndSetIfChanged(ref _sort, value ?? "Artist"); ApplyFilter(); } }
    public string Show { get => _show; set { this.RaiseAndSetIfChanged(ref _show, value ?? "All songs"); ApplyFilter(); } }
    public string Language { get => _language; set { this.RaiseAndSetIfChanged(ref _language, value ?? AnyLanguage); ApplyFilter(); } }

    /// <summary>"Any language" plus the languages in the collection, most songs first.</summary>
    public IReadOnlyList<string> Languages { get; private set; } = new[] { AnyLanguage };

    private bool Shows(SongCardViewModel c) => _show switch
    {
        "New" => c.Cluster.Versions.Any(v => _library.IsNew(v.Entry)),
        "With video" => c.Cluster.Versions.Any(v => v.Entry.VideoPath is not null),
        "Duets" => c.Cluster.Versions.Any(v => v.IsDuet),
        "Community charts" => c.Cluster.Versions.Any(v => !v.IsAi),
        "AI charts" => c.Cluster.Versions.Any(v => v.IsAi),
        "Not sung yet" => c.BestScore is null,
        "Charts to check" => c.Cluster.Versions.Any(v => _library.CheckOf(v.Entry) is { } k && (k.Mismatch || k.LinesToCheck.Count >= SongCardViewModel.LinesWorthAWarning)),
        _ => true,
    };

    private IEnumerable<SongCardViewModel> Sorted(IEnumerable<SongCardViewModel> cards) => _sort switch
    {
        "Title" => cards.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase),
        "Recently added" => cards.OrderByDescending(c => c.AddedUtc),
        "Year" => cards.OrderByDescending(c => c.Entry.Song.Year ?? 0).ThenBy(c => c.Artist, StringComparer.CurrentCultureIgnoreCase),
        "Best score" => cards.OrderByDescending(c => c.BestScore ?? -1),
        _ => cards, // the scan's own order: artist, then title
    };

    private void ApplyFilter()
    {
        var words = SearchText.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Songs.Clear();
        foreach (var card in Sorted(_all.Where(c => words.All(c.SearchKey.Contains) && Shows(c)
                     && (_language == AnyLanguage || string.Equals(c.Entry.Song.Language?.Trim(), _language, StringComparison.OrdinalIgnoreCase)))))
            Songs.Add(card);
    }

    private void RefreshLanguages()
    {
        Languages = new[] { AnyLanguage }.Concat(_all
            .Select(c => c.Entry.Song.Language?.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .GroupBy(l => l!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)).ToList();
        this.RaisePropertyChanged(nameof(Languages));
        if (!Languages.Contains(_language)) Language = AnyLanguage;
    }

    // ── Party queue ───────────────────────────────────────────────────────
    public Singularity.Karaoke.Party.PartyQueue Party { get; }

    /// <summary>Who sings what next, top first.</summary>
    public ObservableCollection<QueuedSongViewModel> UpNext { get; } = new();

    public bool HasQueue => UpNext.Count > 0;

    private string _queueName = "";

    /// <summary>The name a song is queued under from the laptop.</summary>
    public string QueueName { get => _queueName; set => this.RaiseAndSetIfChanged(ref _queueName, value ?? ""); }

    private string? _queueMessage;
    public string? QueueMessage { get => _queueMessage; private set => this.RaiseAndSetIfChanged(ref _queueMessage, value); }

    public ICommand AddToQueueCommand { get; }
    public ICommand SingNextCommand { get; }
    public ICommand RemoveQueuedCommand { get; }
    public ICommand MoveUpQueuedCommand { get; }

    private void RefreshQueue()
    {
        UpNext.Clear();
        int i = 0;
        foreach (var item in Party.Items) UpNext.Add(new QueuedSongViewModel(item, ++i));
        this.RaisePropertyChanged(nameof(HasQueue));
    }

    private void AddToQueue(SongCardViewModel? card)
    {
        if (card is null) return;
        var name = string.IsNullOrWhiteSpace(QueueName) ? _config.KaraokePlayer1Name : QueueName;
        var (added, refused) = Party.Add(card.Entry.Folder, card.Title, card.Artist, name);
        QueueMessage = refused ?? $"{added!.Singer}: {added.Title} is in the queue.";
    }

    /// <summary>Starts the top of the queue (a song no longer in the collection is dropped with a note).</summary>
    private void SingNext()
    {
        if (Party.Next is not { } next) return;
        var entry = _all.SelectMany(c => c.Cluster.Versions).Select(v => v.Entry)
            .FirstOrDefault(e => string.Equals(e.Folder, next.Folder, StringComparison.OrdinalIgnoreCase) && e.IsPlayable);
        Party.Take(next.Id);
        if (entry is null)
        {
            QueueMessage = $"{next.Title} isn't in the collection any more; skipped.";
            return;
        }
        _preview.Stop();
        _sing.StartQueued(entry, next.Singer);
        _navigation.NavigateTo("Sing");
    }

    private void Sing(SongCardViewModel? card)
    {
        if (card is null || !card.Entry.IsPlayable) return;
        _preview.Stop();
        _sing.Start(card.Entry);
        _navigation.NavigateTo("Sing");
    }
}

/// <summary>A line in the "Up next" list.</summary>
public sealed class QueuedSongViewModel
{
    public QueuedSongViewModel(Singularity.Karaoke.Party.QueuedSong item, int place)
    {
        Item = item;
        Place = place;
    }

    public Singularity.Karaoke.Party.QueuedSong Item { get; }
    public int Place { get; }
    public string Singer => Item.Singer;
    public string Song => $"{Item.Title} · {Item.Artist}";
    public bool FromPhone => Item.From == "phone";
}
