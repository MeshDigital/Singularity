using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Singularity.Models;
using Singularity.Services;
using Singularity.Views;
using Avalonia.Controls.Selection;
using Singularity.Data;
using Singularity.Data.Entities;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using Singularity.Events;
using Singularity.Configuration;
using Singularity.Services.Library;

namespace Singularity.ViewModels;

/// <summary>
/// Coordinator ViewModel for the Library page.
/// Delegates responsibilities to child ViewModels following Single Responsibility Principle.
/// </summary>
public partial class LibraryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private bool _isDisposed;

    private readonly ILogger<LibraryViewModel> _logger;
    private readonly INavigationService _navigationService;
    private readonly ImportHistoryViewModel _importHistoryViewModel;
    private readonly ILibraryService _libraryService;
    internal ILibraryService LibraryService => _libraryService;
    private readonly ITaggerService _taggerService;
    private ILifecycleProjectionService _lifecycleProjectionService;
    private readonly IEventBus _eventBus;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    
    // Core Dependencies
    private readonly SpotifyEnrichmentService _spotifyEnrichmentService;
    private readonly LibraryCacheService _libraryCacheService;
    private readonly IServiceProvider? _serviceProvider;
    private readonly DatabaseService _databaseService;
    private readonly SmartCrateService _smartCrateService;
    private readonly DownloadManager _downloadManager;
    private readonly Services.Library.ColumnConfigurationService _columnConfigService;
    private readonly Configuration.AppConfig _appConfig;
    private readonly ConfigManager _configManager;
    private readonly Services.Library.PlaylistExportService _exportService;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    
    public Library.ProjectListViewModel Projects { get; }
    public Library.TrackListViewModel Tracks { get; }
    public Library.TrackOperationsViewModel Operations { get; }
    public Library.SmartPlaylistViewModel SmartPlaylists { get; }
    public System.Collections.ObjectModel.ObservableCollection<ColumnDefinition> AvailableColumns { get; } = new();
    public LibrarySourcesViewModel LibrarySourcesViewModel { get; }
    public Library.LibraryHealthViewModel LibraryHealthViewModel { get; }
    public ImportHistoryViewModel ImportHistoryViewModel => _importHistoryViewModel;

    private Views.MainViewModel? _mainViewModel;
    public Views.MainViewModel? MainViewModel
    {
        get => _mainViewModel;
        private set { _mainViewModel = value; OnPropertyChanged(); }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    private bool _isSourcesOpen = false;
    public bool IsSourcesOpen
    {
        get => _isSourcesOpen;
        set { _isSourcesOpen = value; OnPropertyChanged(); }
    }

    private System.Collections.ObjectModel.ObservableCollection<PlaylistJob> _deletedProjects = new();
    public System.Collections.ObjectModel.ObservableCollection<PlaylistJob> DeletedProjects
    {
        get => _deletedProjects;
        set { _deletedProjects = value; OnPropertyChanged(); }
    }

    private System.Collections.ObjectModel.ObservableCollection<OrphanedTrackViewModel> _orphanedTracks = new();
    public System.Collections.ObjectModel.ObservableCollection<OrphanedTrackViewModel> OrphanedTracks
    {
        get => _orphanedTracks;
        set { _orphanedTracks = value; OnPropertyChanged(); }
    }

    // Expose commonly used child properties for backward compatibility (XAML Bindings)
    public PlaylistJob? SelectedProject 
    { 
        get => Projects.SelectedProject;
        set => Projects.SelectedProject = value;
    }
    
    public System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel> CurrentProjectTracks
    {
        get => Tracks.CurrentProjectTracks;
        set => Tracks.CurrentProjectTracks = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isEditMode;
    public bool IsEditMode
    {
        get => _isEditMode;
        set { _isEditMode = value; OnPropertyChanged(); }
    }

    private bool _isActiveDownloadsVisible;
    public bool IsActiveDownloadsVisible
    {
        get => _isActiveDownloadsVisible;
        set { _isActiveDownloadsVisible = value; OnPropertyChanged(); }
    }

    // Default expanded until user manually chooses a collapsed state.
    private bool _isNavigationCollapsed;
    private int _manualNavigationCollapseCount;
    private int _physicalOnDiskCount;
    private int _indexedCatalogCount;
    private int _staleIndexedCount;
    private int _ingestionBacklogCount;
    private int _desiredDownloadCount;
    private string _libraryLifecycleStatusMessage = string.Empty;

    public bool IsNavigationHoverAutoHideEnabled => _appConfig.LibraryNavigationAutoHideEnabled;

    public bool UseNewPlaylistSurface => _appConfig.UseNewPlaylistSurface;

    public int NavigationHoverAutoHideActivationCount => Math.Max(2, _appConfig.LibraryNavigationAutoHideActivationToggleCount);

    public bool IsNavigationHoverAutoHideArmed =>
        IsNavigationHoverAutoHideEnabled && _manualNavigationCollapseCount >= NavigationHoverAutoHideActivationCount;

    public bool IsNavigationCollapsed
    {
        get => _isNavigationCollapsed;
        set { SetProperty(ref _isNavigationCollapsed, value); }
    }

    private const double CollapsedNavPanelWidth = 60;
    private double _lastExpandedNavPanelWidth = 340;

    /// <summary>User-drag-resizable width of the playlist navigation panel (All Tracks/Liked Songs/Folders/Projects).</summary>
    private double _libraryNavPanelWidth = 340;
    public double LibraryNavPanelWidth
    {
        get => _libraryNavPanelWidth;
        set { SetProperty(ref _libraryNavPanelWidth, value); }
    }

    internal ILogger<LibraryViewModel> Logger => _logger;
    internal PlayerViewModel Player => _playerViewModel;

    public int PhysicalOnDiskCount
    {
        get => _physicalOnDiskCount;
        private set => SetProperty(ref _physicalOnDiskCount, value);
    }

    public int IndexedCatalogCount
    {
        get => _indexedCatalogCount;
        private set => SetProperty(ref _indexedCatalogCount, value);
    }

    public int StaleIndexedCount
    {
        get => _staleIndexedCount;
        private set => SetProperty(ref _staleIndexedCount, value);
    }

    public int IngestionBacklogCount
    {
        get => _ingestionBacklogCount;
        private set => SetProperty(ref _ingestionBacklogCount, value);
    }

    public int DesiredDownloadCount
    {
        get => _desiredDownloadCount;
        private set => SetProperty(ref _desiredDownloadCount, value);
    }

    public string LibraryLifecycleStatusMessage
    {
        get => _libraryLifecycleStatusMessage;
        private set => SetProperty(ref _libraryLifecycleStatusMessage, value);
    }

    public string LibraryCountDifferentiationSummary =>
        $"Wanted downloads: {DesiredDownloadCount} • Ingestion backlog: {IngestionBacklogCount} • Physical on-disk indexed: {PhysicalOnDiskCount} • Stale indexed rows: {StaleIndexedCount}";

    private void RegisterManualNavigationCollapse()
    {
        _manualNavigationCollapseCount++;
        OnPropertyChanged(nameof(IsNavigationHoverAutoHideArmed));
    }

    private bool _isRemovalHistoryVisible;
    public bool IsRemovalHistoryVisible
    {
        get => _isRemovalHistoryVisible;
        set { SetProperty(ref _isRemovalHistoryVisible, value); }
    }

    private bool _isImportHistoryVisible;
    public bool IsImportHistoryVisible
    {
        get => _isImportHistoryVisible;
        set { SetProperty(ref _isImportHistoryVisible, value); }
    }

    private bool _isOrphanedTracksVisible;
    public bool IsOrphanedTracksVisible
    {
        get => _isOrphanedTracksVisible;
        set { SetProperty(ref _isOrphanedTracksVisible, value); }
    }

    private bool _isLibraryHealthVisible;
    public bool IsLibraryHealthVisible
    {
        get => _isLibraryHealthVisible;
        set { SetProperty(ref _isLibraryHealthVisible, value); }
    }

    private double _sidebarWidth = 420;
    public double SidebarWidth
    {
        get => _sidebarWidth;
        set { SetProperty(ref _sidebarWidth, value); }
    }

    private readonly PlayerViewModel _playerViewModel;
    public PlayerViewModel PlayerViewModel => _playerViewModel;
    
    // Track View Customization
    public TrackViewSettings ViewSettings { get; } = new();
    
    // Help Panel
    private bool _isHelpPanelOpen;
    public bool IsHelpPanelOpen
    {
        get => _isHelpPanelOpen;
        set => SetProperty(ref _isHelpPanelOpen, value);
    }

    partial void InitializeCommands();

    public LibraryViewModel(
        ILogger<LibraryViewModel> logger,
        Library.ProjectListViewModel projects,
        Library.TrackListViewModel tracks,
        Library.TrackOperationsViewModel operations,
        Library.SmartPlaylistViewModel smartPlaylists,
        INavigationService navigationService,
        ImportHistoryViewModel importHistoryViewModel,
        ILibraryService libraryService,
        ITaggerService taggerService,
        ILifecycleProjectionService lifecycleProjectionService,
        IEventBus eventBus,
        PlayerViewModel playerViewModel,
        IDialogService dialogService,
        INotificationService notificationService,
        SpotifyEnrichmentService spotifyEnrichmentService,
        LibraryCacheService libraryCacheService,
        LibrarySourcesViewModel librarySourcesViewModel,
        Library.LibraryHealthViewModel libraryHealthViewModel,
        IServiceProvider serviceProvider,
        DatabaseService databaseService,
        SearchFilterViewModel searchFilters,
        SmartCrateService smartCrateService,
        DownloadManager downloadManager,
        Services.Library.ColumnConfigurationService columnConfigService,
        Configuration.AppConfig appConfig,
        ConfigManager configManager,
        Services.Library.PlaylistExportService exportService,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _navigationService = navigationService;
        _importHistoryViewModel = importHistoryViewModel;
        _libraryService = libraryService;
        _taggerService = taggerService;
        _lifecycleProjectionService = lifecycleProjectionService;
        _eventBus = eventBus;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _spotifyEnrichmentService = spotifyEnrichmentService;
        _playerViewModel = playerViewModel;
        _libraryCacheService = libraryCacheService;
        _serviceProvider = serviceProvider;
        _databaseService = databaseService;
        _smartCrateService = smartCrateService;
        _downloadManager = downloadManager;
        _columnConfigService = columnConfigService;
        _appConfig = appConfig;
        _configManager = configManager;
        _exportService = exportService;
        LibrarySourcesViewModel = librarySourcesViewModel;
        LibraryHealthViewModel = libraryHealthViewModel;

        _isNavigationCollapsed = _appConfig.LibraryNavigationCollapsed;
        // Every runtime toggle path (ExecuteToggleNavigation, the hover handlers) sets this flag
        // and the panel width together via CollapseNavPanelWidth/ExpandNavPanelWidth. Restoring
        // the flag from persisted config here is the one path that didn't — so a session that had
        // been left collapsed would reopen with IsNavigationCollapsed=true (content hidden) but
        // LibraryNavPanelWidth still at its unrelated 340 default, leaving a big blank gap where
        // the column's MinWidth correctly shrank to 60 for the (now-hidden) content but its actual
        // Width never followed.
        if (_isNavigationCollapsed)
        {
            _libraryNavPanelWidth = CollapsedNavPanelWidth;
        }

        Projects = projects;
        Tracks = tracks;
        Operations = operations;
        SmartPlaylists = smartPlaylists;

        // Bridge TrackList and Operations for ContextMenu functionality
        Tracks.Operations = operations;

        // Load columns
        _ = InitializeColumnsAsync();

        InitializeCommands();

        // Wire up events
        Projects.ProjectSelected += OnProjectSelected;
        SmartPlaylists.SmartPlaylistSelected += OnSmartPlaylistSelected;
        Tracks.SelectedTracks.CollectionChanged += OnTrackSelectionChanged;


        
        _disposables.Add(_eventBus.GetEvent<ProjectAddedEvent>().Subscribe(OnProjectAdded));
        _disposables.Add(_eventBus.GetEvent<SearchRequestedEvent>().Subscribe(OnSearchRequested));
        _disposables.Add(_eventBus.GetEvent<FileIngestionQueuedEvent>().Subscribe(OnFileIngestionQueued));
        _disposables.Add(_eventBus.GetEvent<FileIngestionStartedEvent>().Subscribe(OnFileIngestionStarted));
        _disposables.Add(_eventBus.GetEvent<FileIngestionCompletedEvent>().Subscribe(OnFileIngestionCompleted));
        _disposables.Add(_eventBus.GetEvent<FileMissingDetectedEvent>().Subscribe(OnFileMissingDetected));
        _disposables.Add(_eventBus.GetEvent<TrackRemovedEvent>().Subscribe(evt => OnLibraryTrackRemoved(evt.TrackGlobalId)));
        _disposables.Add(_eventBus.GetEvent<RemoveTrackFromInspectorEvent>().Subscribe(_ =>
            Dispatcher.UIThread.InvokeAsync(() => Operations.RemoveTrackCommand.Execute(null))));
        _disposables.Add(_eventBus.GetEvent<EditTagsFromInspectorEvent>().Subscribe(_ =>
            Dispatcher.UIThread.InvokeAsync(() => BatchTagEditCommand.Execute(null))));
        
        // Startup background tasks
        Task.Run(() => _libraryService.SyncLibraryEntriesFromTracksAsync()).ConfigureAwait(false);
        _ = RefreshLifecycleMetricsAsync();

    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                _disposables.Dispose();

                Projects.ProjectSelected -= OnProjectSelected;
                SmartPlaylists.SmartPlaylistSelected -= OnSmartPlaylistSelected;
                Tracks.SelectedTracks.CollectionChanged -= OnTrackSelectionChanged;
            }
            _isDisposed = true;
        }
    }

    public void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void SetMainViewModel(Views.MainViewModel mainViewModel)
    {
        MainViewModel = mainViewModel;
        if (Tracks != null)
            Tracks.SetMainViewModel(mainViewModel);
        Operations?.SetMainViewModel(mainViewModel);
    }

    public void AddToPlaylist(PlaylistJob targetPlaylist, PlaylistTrackViewModel track)
    {
        // Drag/drop invokes this compatibility entry point; playlist add flow is owned elsewhere.
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private LibraryEntry MapEntityToLibraryEntry(PlaylistTrackEntity entity)
    {
        return new LibraryEntry
        {
            UniqueHash = entity.TrackUniqueHash,
            FilePath = entity.ResolvedFilePath,
            Title = entity.Title,
            Artist = entity.Artist,
            Album = entity.Album,
            Genres = entity.Genres,
            BPM = entity.BPM,
            MusicalKey = entity.MusicalKey,
            Bitrate = entity.Bitrate
        };
    }

    private void OnSearchRequested(SearchRequestedEvent evt)
    {
        _logger.LogInformation("🔍 Cross-Component Search Requested: {Query}", evt.Query);
        
        // Use Dispatcher to ensure UI updates on main thread
        Avalonia.Threading.Dispatcher.UIThread.Post(() => 
        {
            // Update search query
            Tracks.SearchText = evt.Query?.Trim() ?? string.Empty;
            
            // Clear project selection to show "All Tracks"
            Projects.SelectedProject = null;

            // Force immediate refresh so the DataGrid reflects ad-hoc query requests reliably.
            Tracks.RefreshFilteredTracks();
            
            // Ensure the user lands on the Library view for ad-hoc search results.
            MainViewModel?.NavigateLibraryCommand?.Execute(null);
        });
    }

    private async Task InitializeColumnsAsync()
    {
        var columns = await _columnConfigService.LoadConfigurationAsync();
        foreach (var col in columns.OrderBy(c => c.DisplayOrder))
        {
            AvailableColumns.Add(col);
        }
    }

    private async Task RefreshLifecycleMetricsAsync()
    {
        try
        {
            var metrics = await _lifecycleProjectionService.ComputeMetricsAsync().ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyLifecycleMetrics(metrics);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh library lifecycle metrics");
        }
    }

    private void OnFileIngestionQueued(FileIngestionQueuedEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
            ApplyFileIngestionQueued(evt));
    }

    private void OnFileIngestionStarted(FileIngestionStartedEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
            ApplyFileIngestionStarted(evt));
    }

    private void OnFileIngestionCompleted(FileIngestionCompletedEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
            ApplyFileIngestionCompleted(evt));
    }

    private void OnFileMissingDetected(FileMissingDetectedEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
            ApplyFileMissingDetected(evt));
    }

    private void ApplyFileIngestionQueued(FileIngestionQueuedEvent evt)
    {
        var next = _lifecycleProjectionService.ApplyFileIngestionQueued(GetCurrentLifecycleMetrics());

        ApplyLifecycleMetrics(next);
        LibraryLifecycleStatusMessage = $"Ingestion pending: {System.IO.Path.GetFileName(evt.FilePath)}";
    }

    private void ApplyFileIngestionStarted(FileIngestionStartedEvent evt)
    {
        LibraryLifecycleStatusMessage = $"Ingestion started: {System.IO.Path.GetFileName(evt.FilePath)}";
    }

    private void ApplyFileIngestionCompleted(FileIngestionCompletedEvent evt)
    {
        var next = _lifecycleProjectionService.ApplyFileIngestionCompleted(GetCurrentLifecycleMetrics());

        ApplyLifecycleMetrics(next);
        LibraryLifecycleStatusMessage = $"Indexed: {System.IO.Path.GetFileName(evt.FilePath)}";
    }

    private void ApplyFileMissingDetected(FileMissingDetectedEvent evt)
    {
        var next = _lifecycleProjectionService.ApplyFileMissingDetected(GetCurrentLifecycleMetrics());

        ApplyLifecycleMetrics(next);
        LibraryLifecycleStatusMessage = $"Stale index detected: {System.IO.Path.GetFileName(evt.FilePath)}";
    }

    private LifecycleMetrics GetCurrentLifecycleMetrics()
    {
        return new LifecycleMetrics(
            PhysicalOnDiskCount,
            IndexedCatalogCount,
            StaleIndexedCount,
            IngestionBacklogCount,
            DesiredDownloadCount);
    }

    private void ApplyLifecycleMetrics(LifecycleMetrics metrics)
    {
        PhysicalOnDiskCount = metrics.PhysicalOnDisk;
        IndexedCatalogCount = metrics.IndexedCatalog;
        StaleIndexedCount = metrics.StaleIndexed;
        IngestionBacklogCount = metrics.IngestionBacklog;
        DesiredDownloadCount = metrics.DesiredDownloads;
        OnPropertyChanged(nameof(LibraryCountDifferentiationSummary));
    }
}
