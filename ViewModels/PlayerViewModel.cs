using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Singularity.Configuration;
using Singularity.Models;
using Singularity.Services;
using Singularity.Views;

// using DraggingService; // TODO: Fix drag-drop library reference

using System.Reactive; // Added for ReactiveCommand<Unit, Unit>
using System.Reactive.Linq;
using System.Reactive.Disposables;
using ReactiveUI;

namespace Singularity.ViewModels
{
    using System.ComponentModel;
    using System.Runtime.CompilerServices;

    public partial class PlayerViewModel : INotifyPropertyChanged, IDisposable
    {

        private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();
        private bool _isDisposed;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
        private readonly IAudioPlayerService _playerService;
        private readonly AppConfig? _config;
        private readonly ConfigManager? _configManager;
        private readonly IDialogService? _dialogService;
        // Singletons — stopped whenever real queue/playlist playback starts (see LoadTrackCore),
        // so a Mix Editor waveform click-preview or a Library row hover-preview never keeps
        // playing underneath the track the user actually pressed play on. Both are self-contained
        // WASAPI outputs (never hijack this main player), so nothing crashes if they overlap —
        // they'd just audibly mix together, which is the actual problem this prevents.
        private readonly Singularity.Services.Audio.ILibraryPreviewPlayer? _libraryPreviewPlayer;

        // Waveform appearance pass-through — set once from AppConfig in the constructor.
        public bool WaveformUseNeonPalette { get; }
        public double WaveformGain { get; }
        public bool WaveformShowEnergyCurve { get; }
        public bool WaveformShowVocalGhost { get; }
        private readonly DatabaseService _databaseService;
        private readonly ArtworkCacheService _artworkCacheService;
        private readonly IEventBus _eventBus;
        private readonly INavigationService _navigationService;
        private readonly IRightPanelService _rightPanelService;
        private readonly System.Threading.Timer _saveQueueTimer;
        private bool _suppressSave;
        private System.Threading.CancellationTokenSource? _errorDismissCts;
        private bool _playerNavigationInFlight;
        private DateTime _lastPlayerNavigationRequestUtc = DateTime.MinValue;

        private static readonly TimeSpan PlayerNavigationDebounceWindow = TimeSpan.FromMilliseconds(350);
        private static readonly TimeSpan PlayerNavigationSettleDelay = TimeSpan.FromMilliseconds(150);

        
        private string _trackTitle = "No Track Playing";
        public string TrackTitle
        {
            get => _trackTitle;
            set => SetProperty(ref _trackTitle, value);
        }

        private string _trackArtist = "";
        public string TrackArtist
        {
            get => _trackArtist;
            set => SetProperty(ref _trackArtist, value);
        }

        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetProperty(ref _isPlaying, value);
        }

        private float _position; // 0.0 to 1.0
        public float Position
        {
            get => _position;
            set => SetProperty(ref _position, value);
        }

        private string _currentTimeStr = "0:00";
        public string CurrentTimeStr
        {
            get => _currentTimeStr;
            set => SetProperty(ref _currentTimeStr, value);
        }

        private string _totalTimeStr = "0:00";
        public string TotalTimeStr
        {
            get => _totalTimeStr;
            set => SetProperty(ref _totalTimeStr, value);
        }
        
        private int _volume = 100;
        public int Volume
        {
            get => _volume;
            set
            {
                if (SetProperty(ref _volume, value))
                {
                    OnVolumeChanged(value);
                    OnPropertyChanged(nameof(VolumeIcon));
                }
            }
        }

        private bool _isMuted;
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (SetProperty(ref _isMuted, value))
                {
                    OnPropertyChanged(nameof(VolumeIcon));
                }
            }
        }

        private int _preMuteVolume = 100;

        /// <summary>Returns the appropriate speaker icon based on mute state and volume level.</summary>
        public string VolumeIcon
        {
            get
            {
                if (_isMuted || _volume == 0) return "🔇";
                if (_volume < 33) return "🔈";
                if (_volume < 66) return "🔉";
                return "🔊";
            }
        }

        private long _lengthMs;
        public long LengthMs
        {
            get => _lengthMs;
            set => SetProperty(ref _lengthMs, value);
        }

        private bool _isPlayerInitialized;
        public bool IsPlayerInitialized
        {
            get => _isPlayerInitialized;
            set => SetProperty(ref _isPlayerInitialized, value);
        }
        
        // Queue Management
        public ObservableCollection<PlaylistTrackViewModel> Queue { get; } = new();

        /// <summary>Returns true when the queue has no tracks.</summary>
        public bool IsQueueEmpty => !Queue.Any();

        /// <summary>The next couple of tracks after the one currently playing — a glanceable
        /// "up next" strip that doesn't require opening the full queue panel.</summary>
        public List<PlaylistTrackViewModel> UpNextPreview =>
            Queue.Skip(CurrentQueueIndex + 1).Take(2).ToList();

        public bool HasUpNext => CurrentQueueIndex + 1 < Queue.Count;

        /// <summary>Everything after the playing track, in play order (fullscreen player's Up Next).</summary>
        public List<PlaylistTrackViewModel> UpNextQueue =>
            Queue.Skip(Math.Max(0, CurrentQueueIndex + 1)).ToList();

        /// <summary>"12 up next · 58 min" — or empty when nothing is queued after the current track.</summary>
        public string UpNextSummary
        {
            get
            {
                var upcoming = Queue.Skip(Math.Max(0, CurrentQueueIndex + 1)).ToList();
                if (upcoming.Count == 0) return string.Empty;
                var ms = upcoming.Sum(t => (long)(t.Model?.CanonicalDuration ?? 0));
                var minutes = (int)Math.Round(ms / 60000.0);
                return minutes > 0 ? $"{upcoming.Count} up next · {minutes} min" : $"{upcoming.Count} up next";
            }
        }

        /// <summary>"Track 3 of 14" while playing from the queue.</summary>
        public string QueuePositionText =>
            CurrentQueueIndex >= 0 && CurrentQueueIndex < Queue.Count ? $"Track {CurrentQueueIndex + 1} of {Queue.Count}" : string.Empty;

        private bool _isExpandedQueueVisible = true;
        /// <summary>Up Next column in the fullscreen player; shown by default whenever there is a queue.</summary>
        public bool IsExpandedQueueVisible
        {
            get => _isExpandedQueueVisible;
            set
            {
                if (SetProperty(ref _isExpandedQueueVisible, value))
                    OnPropertyChanged(nameof(ShowExpandedQueue));
            }
        }

        public bool ShowExpandedQueue => IsExpandedQueueVisible && Queue.Count > 0;

        private bool _queueStatesScheduled;

        /// <summary>Coalesces queue/index changes into one pass over the rows (bulk loads add one row at a time).</summary>
        private void ScheduleUpdateQueueStates()
        {
            if (_queueStatesScheduled) return;
            _queueStatesScheduled = true;
            Dispatcher.UIThread.Post(() =>
            {
                _queueStatesScheduled = false;
                UpdateQueueStates();
            }, DispatcherPriority.Background);
        }

        /// <summary>Marks each queue row as played / playing / upcoming and numbers it.</summary>
        internal void UpdateQueueStates()
        {
            var index = CurrentQueueIndex;
            for (int i = 0; i < Queue.Count; i++)
            {
                var row = Queue[i];
                row.QueueNumber = i + 1;
                row.IsQueueCurrent = i == index;
                row.IsQueuePlayed = index >= 0 && i < index;
            }
            OnPropertyChanged(nameof(UpNextQueue));
            OnPropertyChanged(nameof(UpNextSummary));
            OnPropertyChanged(nameof(QueuePositionText));
        }

        // ── Mix transition visibility ────────────────────────────────────────────────────────
        // Previously AudioPlayerService computed all of this (whether a crossfade was active, how
        // far through it was, which preset) with zero external visibility — no UI could show
        // "mixing now" during real playback. Wired to AudioPlayerService.CrossfadeStarted/
        // CrossfadeProgressChanged/CrossfadeEnded in the constructor below.

        private bool _isCrossfading;
        /// <summary>True while the engine is actively crossfading into the next track.</summary>
        public bool IsCrossfading
        {
            get => _isCrossfading;
            set => SetProperty(ref _isCrossfading, value);
        }

        private double _crossfadeProgressPercent;
        /// <summary>0-100 progress through the active crossfade.</summary>
        public double CrossfadeProgressPercent
        {
            get => _crossfadeProgressPercent;
            set => SetProperty(ref _crossfadeProgressPercent, value);
        }

        private int _currentQueueIndex = -1;
        public int CurrentQueueIndex
        {
            get => _currentQueueIndex;
            set
            {
                if (SetProperty(ref _currentQueueIndex, value))
                {
                    OnPropertyChanged(nameof(UpNextPreview));
                    OnPropertyChanged(nameof(HasUpNext));
                    ScheduleUpdateQueueStates();
                }
            }
        }
        
        private PlaylistTrackViewModel? _currentTrack;
        public PlaylistTrackViewModel? CurrentTrack
        {
            get => _currentTrack;
            set
            {
                var previousTrack = _currentTrack;
                if (SetProperty(ref _currentTrack, value))
                {
                    AttachCurrentTrackObservers(previousTrack, value);
                    RaiseCurrentTrackSummaryProperties();
                    UpdateCurrentVideo();
                }
            }
        }

        // ── Music video ────────────────────────────────────────────────────────
        private readonly Singularity.Services.Karaoke.KaraokeLibrary? _karaokeLibrary;
        private Singularity.Services.Karaoke.KaraokeVideo? _currentVideo;

        /// <summary>The playing track's music video, when it is (or was made into) a karaoke song with one.</summary>
        public string? CurrentVideoPath => _currentVideo?.Path;
        public double CurrentVideoGapMs => _currentVideo?.GapMs ?? 0;
        public bool HasCurrentVideo => _currentVideo is not null;

        /// <summary>Where the music is, for the video surface.</summary>
        public Func<double?> VideoClock { get; }

        private void UpdateCurrentVideo()
        {
            _currentVideo = _karaokeLibrary?.FindVideo(_currentTrack?.Model.ResolvedFilePath);
            OnPropertyChanged(nameof(CurrentVideoPath));
            OnPropertyChanged(nameof(CurrentVideoGapMs));
            OnPropertyChanged(nameof(HasCurrentVideo));
        }

        /// <summary>
        /// Waveform data for the currently playing track, forwarded from <see cref="CurrentTrack"/>.
        /// Returns <see langword="null"/> when no track is loaded.
        /// </summary>
        public WaveformAnalysisData? WaveformData => _currentTrack?.WaveformData;
        public bool HasCurrentTrack => _currentTrack is not null;

        public string CurrentTrackContextSummary => BuildTrackContextSummary(_currentTrack);
        public string CurrentTrackKeyBadge => _currentTrack is null || string.IsNullOrWhiteSpace(_currentTrack.CamelotDisplay) || _currentTrack.CamelotDisplay == "—"
            ? "KEY —"
            : $"KEY {_currentTrack.CamelotDisplay}";
        
        // Shuffle & Repeat
        private bool _isShuffling;
        public bool IsShuffling
        {
            get => _isShuffling;
            set { if (SetProperty(ref _isShuffling, value)) SchedulePreloadNext(); }
        }

        private RepeatMode _repeatMode = RepeatMode.Off;
        public RepeatMode RepeatMode
        {
            get => _repeatMode;
            set { if (SetProperty(ref _repeatMode, value)) SchedulePreloadNext(); }
        }

        /// <summary>Gapless/crossfade: when enabled, the preloaded next track overlaps and
        /// fades in while the current one fades out, instead of a hard cut at track boundaries.</summary>
        public bool IsCrossfadeEnabled
        {
            get => _playerService.CrossfadeEnabled;
            set
            {
                if (_playerService.CrossfadeEnabled == value) return;
                _playerService.CrossfadeEnabled = value;
                OnPropertyChanged();
                PersistPlaybackSettings(c => c.PlaybackCrossfadeEnabled = value);
            }
        }

        /// <summary>Length of the crossfade overlap, in seconds. Only used when
        /// <see cref="IsCrossfadeEnabled"/> is true.</summary>
        public double CrossfadeSeconds
        {
            get => _playerService.CrossfadeSeconds;
            set
            {
                if (Math.Abs(_playerService.CrossfadeSeconds - value) < 0.01) return;
                _playerService.CrossfadeSeconds = value;
                OnPropertyChanged();
                PersistPlaybackSettings(c => c.PlaybackCrossfadeSeconds = value);
            }
        }
        
        // Player Dock Location
        private PlayerDockLocation _currentDockLocation = PlayerDockLocation.RightSidebar;
        public PlayerDockLocation CurrentDockLocation
        {
            get => _currentDockLocation;
            set => SetProperty(ref _currentDockLocation, value);
        }
        
        private bool _isPlayerVisible = true;
        public bool IsPlayerVisible
        {
            get => _isPlayerVisible;
            set => SetProperty(ref _isPlayerVisible, value);
        }
        
        // Queue Visibility
        private bool _isQueueOpen;
        public bool IsQueueOpen
        {
            get => _isQueueOpen;
            set => SetProperty(ref _isQueueOpen, value);
        }

        private bool _isTheaterMode;
        public bool IsTheaterMode
        {
            get => _isTheaterMode;
            set => SetProperty(ref _isTheaterMode, value);
        }

        // ── Entertainment Engine Properties ─────────────────────────────────

        private bool _isExpandedPlayerOpen;
        /// <summary>True when the full visualizer-first expanded player is visible.</summary>
        public bool IsExpandedPlayerOpen
        {
            get => _isExpandedPlayerOpen;
            set => SetProperty(ref _isExpandedPlayerOpen, value);
        }

        // Phase 9.2: Loading & Error States
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        private bool _hasPlaybackError;
        public bool HasPlaybackError
        {
            get => _hasPlaybackError;
            set => SetProperty(ref _hasPlaybackError, value);
        }

        private string _playbackError = string.Empty;
        public string PlaybackError
        {
            get => _playbackError;
            set => SetProperty(ref _playbackError, value);
        }

        // Phase 9.2: Album Artwork
        private string? _albumArtUrl;
        public string? AlbumArtUrl
        {
            get => _albumArtUrl;
            set => SetProperty(ref _albumArtUrl, value);
        }

        // Phase 9.3: Like Feature
        private bool _isCurrentTrackLiked;
        public bool IsCurrentTrackLiked
        {
            get => _isCurrentTrackLiked;
            set => SetProperty(ref _isCurrentTrackLiked, value);
        }
        
        // Sprint B: High-Fidelity Features
        private float _vuLeft;
        public float VuLeft
        {
            get => _vuLeft;
            set => SetProperty(ref _vuLeft, value);
        }

        private float[] _spectrumData = Array.Empty<float>();
        public float[] SpectrumData
        {
            get => _spectrumData;
            set => SetProperty(ref _spectrumData, value);
        }

        private float[] _waveformSamples = Array.Empty<float>();
        /// <summary>Latest mono time-domain block (oscilloscope/phase visuals).</summary>
        public float[] WaveformSamples
        {
            get => _waveformSamples;
            set => SetProperty(ref _waveformSamples, value);
        }

        private float _vuRight;
        public float VuRight
        {
            get => _vuRight;
            set => SetProperty(ref _vuRight, value);
        }



        private double _pitch = 1.0;
        public double Pitch
        {
            get => _pitch;
            set
            {
                if (SetProperty(ref _pitch, value))
                {
                    _playerService.Pitch = value;
                    PersistPlaybackSettings(c => c.PlaybackPitch = value);
                }
            }
        }

        /// <summary>Writes a playback setting change to disk. Fire-and-forget, same pattern as
        /// other ViewModels' incidental settings saves (e.g. DownloadManager, SearchViewModel).</summary>
        private void PersistPlaybackSettings(Action<AppConfig> apply)
        {
            if (_config == null || _configManager == null) return;
            apply(_config);
            _ = _configManager.SaveAsync(_config);
        }
        
        // Shuffle history to prevent immediate repeats
        private readonly List<int> _shuffleHistory = new();

        public ICommand TogglePlayPauseCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand NextTrackCommand { get; }
        public ICommand PreviousTrackCommand { get; }
        public ICommand AddToQueueCommand { get; }
        public ICommand RemoveFromQueueCommand { get; }

        public ICommand ClearQueueCommand { get; }
        public ICommand ToggleShuffleCommand { get; }
        public ICommand ToggleRepeatCommand { get; }
        public ICommand ToggleCrossfadeCommand { get; }
        public ICommand TogglePlayerDockCommand { get; }
        public ICommand ToggleQueueCommand { get; }
        public ICommand ToggleLikeCommand { get; } // Phase 9.3
        public ICommand ToggleMuteCommand { get; }
        public ICommand ResetPitchCommand { get; }
        public ICommand SeekCommand { get; } // Phase 12.6: Waveform Seeking
        public ICommand SeekForwardCommand { get; }
        public ICommand SeekBackwardCommand { get; }
        public ICommand ToggleTheaterModeCommand { get; }
        public ICommand GoBackCommand { get; } // NowPlayingPage back navigation
        public ICommand OpenPlayerViewCommand { get; }
        public ICommand OpenCurrentTrackInspectorCommand { get; }
        public ICommand RevealCurrentTrackCommand { get; }
        public ICommand AddCurrentTrackToProjectCommand { get; }
        public ICommand PlayQueueItemCommand { get; }

        public ICommand ToggleExpandedPlayerCommand { get; }
        public ICommand ToggleExpandedQueueCommand { get; private set; } = null!;

        // Phase 5C: UI Throttling
        private DateTime _lastTimeUpdate = DateTime.MinValue;

        public PlayerViewModel(IAudioPlayerService playerService, DatabaseService databaseService, IEventBus eventBus, ArtworkCacheService artworkCacheService, INavigationService navigationService, IRightPanelService rightPanelService, AppConfig? config = null, ConfigManager? configManager = null, IDialogService? dialogService = null, Singularity.Services.Audio.ILibraryPreviewPlayer? libraryPreviewPlayer = null,
            Singularity.Services.Karaoke.KaraokeLibrary? karaokeLibrary = null)
        {
            _karaokeLibrary = karaokeLibrary;
            VideoClock = () => _currentVideo is null ? null : _playerService.Time;
            _playerService = playerService;
            _databaseService = databaseService;
            _artworkCacheService = artworkCacheService;
            _eventBus = eventBus;
            _navigationService = navigationService;
            _rightPanelService = rightPanelService;
            _config = config;
            _configManager = configManager;
            _dialogService = dialogService;
            _libraryPreviewPlayer = libraryPreviewPlayer;

            // Restore persisted playback settings (crossfade/pitch used to reset to defaults every restart)
            if (_config != null)
            {
                _playerService.CrossfadeEnabled = _config.PlaybackCrossfadeEnabled;
                _playerService.CrossfadeSeconds = _config.PlaybackCrossfadeSeconds;
                _pitch = _config.PlaybackPitch;
                _playerService.Pitch = _config.PlaybackPitch;
            }

            // Waveform appearance — read-only pass-through to AppConfig, same convention as every
            // other settings-backed value in this app (applies on next view load, no live cross-VM
            // push notification, matching how other settings toggles already behave here).
            WaveformUseNeonPalette = !string.Equals(_config?.WaveformPalette, "ClassicRgb", StringComparison.OrdinalIgnoreCase);
            WaveformGain = _config?.WaveformGain ?? 1.0f;
            WaveformShowEnergyCurve = _config?.WaveformShowEnergyCurve ?? true;
            WaveformShowVocalGhost = _config?.WaveformShowVocalGhost ?? true;

            _saveQueueTimer = new System.Threading.Timer(_ => 
            {
                _ = SaveQueueAsync();
            }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            
            // Phase 6B: Subscribe to playback requests
            eventBus.GetEvent<PlayTrackRequestEvent>().Subscribe(evt => 
            {
                if (evt.Track != null && !string.IsNullOrEmpty(evt.Track.Model.ResolvedFilePath))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        // 1. Check if track is already in queue
                        var existing = Queue.FirstOrDefault(t => t.Model.TrackUniqueHash == evt.Track.Model.TrackUniqueHash);
                        if (existing != null)
                        {
                            var index = Queue.IndexOf(existing);
                            CurrentQueueIndex = index;
                            PlayTrackAtIndex(index);
                        }
                        else
                        {
                            // 2. Add to queue and play
                            Queue.Add(evt.Track);
                            var index = Queue.Count - 1;
                            CurrentQueueIndex = index;
                            PlayTrackAtIndex(index);
                        }
                    });
                }
            }).DisposeWith(_disposables);

            eventBus.GetEvent<AddToQueueRequestEvent>().Subscribe(evt => 
            {
                if (evt.Track != null)
                {
                    AddToQueue(evt.Track);
                }
            }).DisposeWith(_disposables);

            // Phase 6B: Play Album Request (Queue Management)
            eventBus.GetEvent<PlayAlbumRequestEvent>().Subscribe(evt =>
            {
                if (evt.Tracks == null || !evt.Tracks.Any()) return;

                Dispatcher.UIThread.Post(() =>
                {
                    // Starting a whole playlist's worth of playback is exactly a "Mix session" —
                    // default to the bottom playbar (Spotify-style) instead of leaving whatever
                    // dock the player happened to be in, so the Up Next/Mix-badge queue strip is
                    // immediately visible without the user having to switch layouts themselves.
                    CurrentDockLocation = PlayerDockLocation.BottomBar;

                    _suppressSave = true;
                    try
                    {
                        // 1. Clear existing queue — inlined rather than calling the public
                        // ClearQueue() helper, which itself does another Dispatcher.UIThread.Post.
                        // Posting from inside a callback that's already running via Post doesn't
                        // run synchronously — it queues a SEPARATE callback to run only after this
                        // entire block (including the "add all tracks" loop and PlayTrackAtIndex
                        // below) has already finished. That meant the "clear" actually ran AFTER
                        // the fresh queue was populated and playback had already started, silently
                        // wiping the queue and stopping playback moments later (observed as a
                        // "Saved queue with 36 items" DB write immediately followed by a "Saved
                        // queue with 0 items" one a second or two after). We're already on the UI
                        // thread here (inside the outer Post), so just mutate directly.
                        Queue.Clear();
                        CurrentQueueIndex = -1;
                        CurrentTrack = null;
                        _shuffleHistory.Clear();
                        Stop();

                        // 2. Add all tracks to queue
                        foreach (var track in evt.Tracks)
                        {
                            // A non-empty ResolvedFilePath alone isn't enough — it can go stale
                            // (file moved/deleted after the path was resolved) without the row's
                            // other fields ever being corrected. Confirmed live: a track with a
                            // stale path made it into the queue at index 0, LoadTrackCore threw
                            // "Could not find file", and playback silently stopped right there —
                            // "Playing Album" had already fired, so the only visible symptom was a
                            // notification with no audio. SchedulePreloadNext already guards the
                            // same way for the same reason; this brings the initial queue build up
                            // to that same standard.
                            if (!string.IsNullOrEmpty(track.ResolvedFilePath) && System.IO.File.Exists(track.ResolvedFilePath))
                            {
                                var vm = new PlaylistTrackViewModel(track, eventBus, null, _artworkCacheService);
                                Queue.Add(vm);
                            }
                        }
                    }
                    finally
                    {
                        _suppressSave = false;
                        DebounceSaveQueue();
                    }
                    
                    // 3. Play the requested start track (or the first) if any were added
                    if (Queue.Any())
                    {
                        int startIndex = 0;
                        if (evt.StartTrackId is Guid startId)
                        {
                            var found = Queue.ToList().FindIndex(t => t.Model?.Id == startId);
                            if (found >= 0) startIndex = found;
                        }
                        CurrentQueueIndex = startIndex;
                        PlayTrackAtIndex(startIndex);

                    }
                    else
                    {
                        // Every candidate track's ResolvedFilePath turned out stale (file moved or
                        // deleted since the path was resolved) — nothing playable made it into the
                        // queue. "Playing Album" already fired before this event was processed, so
                        // without this the user sees that toast and then silence with zero clue why.
                        Console.WriteLine($"[PlayerViewModel] PlayAlbumRequestEvent had {evt.Tracks.Count()} track(s) but none had a file that still exists on disk");
                    }
                });
            }).DisposeWith(_disposables);
            
            // Ensure IsPlaying is synced
            IsPlaying = _playerService.IsPlaying;
            
            // Phase 9.6: Removed premature check for IsPlayerInitialized. 
            // AudioPlayerService initializes lazily, so this check was always failing on startup.
            // We now rely on Play() to trigger init and handle errors there.
            
            // Player Service Events via Reactive patterns to ensure cleanup
            Observable.FromEventPattern(h => _playerService.PausableChanged += h, h => _playerService.PausableChanged -= h)
                .Subscribe(_ => Dispatcher.UIThread.Post(() =>
                {
                    IsPlaying = _playerService.IsPlaying;
                }))
                .DisposeWith(_disposables);

            Observable.FromEventPattern(h => _playerService.EndReached += h, h => _playerService.EndReached -= h)
                .Subscribe(e => Dispatcher.UIThread.Post(() => OnEndReached(e.Sender, EventArgs.Empty)))
                .DisposeWith(_disposables);

            // Gapless/crossfade: the engine advanced to a preloaded track on its own (either a
            // hard gapless swap or a completed crossfade) — sync our "now playing" state to
            // match without calling Play() again, since the audio is already running.
            Observable.FromEventPattern(h => _playerService.TrackAdvanced += h, h => _playerService.TrackAdvanced -= h)
                .Subscribe(_ => Dispatcher.UIThread.Post(OnTrackAdvanced))
                .DisposeWith(_disposables);

            // Crossfade visibility during real playback — see IsCrossfading/CrossfadeProgressPercent.
            Observable.FromEventPattern<CrossfadeStartedEventArgs>(h => _playerService.CrossfadeStarted += h, h => _playerService.CrossfadeStarted -= h)
                .Subscribe(e => Dispatcher.UIThread.Post(() =>
                {
                    IsCrossfading = true;
                    CrossfadeProgressPercent = 0;
                }))
                .DisposeWith(_disposables);

            Observable.FromEventPattern<double>(h => _playerService.CrossfadeProgressChanged += h, h => _playerService.CrossfadeProgressChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(50))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e => CrossfadeProgressPercent = e.EventArgs * 100.0)
                .DisposeWith(_disposables);

            Observable.FromEventPattern(h => _playerService.CrossfadeEnded += h, h => _playerService.CrossfadeEnded -= h)
                .Subscribe(_ => Dispatcher.UIThread.Post(() =>
                {
                    IsCrossfading = false;
                    CrossfadeProgressPercent = 0;
                }))
                .DisposeWith(_disposables);

            Observable.FromEventPattern<float>(h => _playerService.PositionChanged += h, h => _playerService.PositionChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(50)) // 20fps for progress markers
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e => Position = e.EventArgs)
                .DisposeWith(_disposables);

            Observable.FromEventPattern<long>(h => _playerService.TimeChanged += h, h => _playerService.TimeChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(250)) // 4fps for time text is plenty
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e => CurrentTimeStr = TimeSpan.FromMilliseconds(e.EventArgs).ToString(@"m\:ss"))
                .DisposeWith(_disposables);

            Observable.FromEventPattern<long>(h => _playerService.LengthChanged += h, h => _playerService.LengthChanged -= h)
                .Subscribe(e => Dispatcher.UIThread.Post(() => 
                {
                    LengthMs = e.EventArgs;
                    TotalTimeStr = TimeSpan.FromMilliseconds(e.EventArgs).ToString(@"m\:ss");
                }))
                .DisposeWith(_disposables);



            Observable.FromEventPattern<AudioLevelsEventArgs>(h => _playerService.AudioLevelsChanged += h, h => _playerService.AudioLevelsChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(40)) // 25fps for VU meters - visually smooth but efficient
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e =>
                {
                    VuLeft = e.EventArgs.Left;
                    VuRight = e.EventArgs.Right;
                })
                .DisposeWith(_disposables);

            Observable.FromEventPattern<float[]>(h => _playerService.SpectrumChanged += h, h => _playerService.SpectrumChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(25)) // FFT blocks arrive ~43/s (50% overlap); the visualizer smooths at display rate
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e => SpectrumData = e.EventArgs)
                .DisposeWith(_disposables);

            Observable.FromEventPattern<float[]>(h => _playerService.WaveformChanged += h, h => _playerService.WaveformChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(33))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e => WaveformSamples = e.EventArgs)
                .DisposeWith(_disposables);

            TogglePlayPauseCommand = new RelayCommand(TogglePlayPause);
            StopCommand = new RelayCommand(Stop);
            NextTrackCommand = new RelayCommand(PlayNextTrack, () => HasNextTrack());
            PreviousTrackCommand = new RelayCommand(PlayPreviousTrack, () => HasPreviousTrack());
            AddToQueueCommand = new RelayCommand<PlaylistTrackViewModel>(AddToQueue);
            RemoveFromQueueCommand = new RelayCommand<PlaylistTrackViewModel>(RemoveFromQueue);
            ClearQueueCommand = new AsyncRelayCommand(ClearQueueWithConfirmationAsync, () => Queue.Any());
            ToggleShuffleCommand = new RelayCommand(ToggleShuffle);
            ToggleRepeatCommand = new RelayCommand(ToggleRepeat);
            ToggleCrossfadeCommand = new RelayCommand(() => IsCrossfadeEnabled = !IsCrossfadeEnabled);
            TogglePlayerDockCommand = new RelayCommand(TogglePlayerDock);
            ToggleQueueCommand = new RelayCommand(ToggleQueue);
            ToggleLikeCommand = new AsyncRelayCommand(ToggleLikeAsync); // Phase 9.3
            ToggleMuteCommand = new RelayCommand(ToggleMute);
            ResetPitchCommand = new RelayCommand(() => Pitch = 1.0);
            SeekCommand = new RelayCommand<float>(Seek);
            SeekForwardCommand = new RelayCommand(() => SeekRelative(10)); // Seek forward 10 seconds
            SeekBackwardCommand = new RelayCommand(() => SeekRelative(-10)); // Seek backward 10 seconds
            ToggleTheaterModeCommand = new RelayCommand(() => _eventBus.Publish(new RequestTheaterModeEvent()));
            GoBackCommand = new RelayCommand(() => _eventBus.Publish(new NavigateToPageEvent("Library")));
            OpenPlayerViewCommand = new RelayCommand(() =>
            {
                _ = OpenPlayerViewAsync();
            });
            OpenCurrentTrackInspectorCommand = new RelayCommand(OpenCurrentTrackInspector);
            RevealCurrentTrackCommand = new RelayCommand(RevealCurrentTrack);
            AddCurrentTrackToProjectCommand = new RelayCommand(AddCurrentTrackToProject);
            PlayQueueItemCommand = new RelayCommand<PlaylistTrackViewModel>(PlayQueueItem);

            ToggleExpandedPlayerCommand = new RelayCommand(() =>
            {
                try
                {
                    if (CurrentTrack == null)
                    {
                        PlaybackError = "Load a track before opening the expanded player.";
                        HasPlaybackError = true;
                        IsExpandedPlayerOpen = false;
                        return;
                    }

                    var shouldOpen = !IsExpandedPlayerOpen;
                    // Closing while in Theater Mode (borderless fullscreen) must leave that too,
                    // or the window stays chrome-less with nothing on screen to get out of it.
                    if (!shouldOpen && IsTheaterMode)
                    {
                        _eventBus.Publish(new RequestTheaterModeEvent());
                        return;
                    }
                    IsExpandedPlayerOpen = shouldOpen;

                    if (shouldOpen)
                    {
                        IsQueueOpen = false;
                        _rightPanelService.IsPanelOpen = false;
                    }
                }
                catch (Exception)
                {
                    PlaybackError = "Could not open the expanded player.";
                    HasPlaybackError = true;
                    IsExpandedPlayerOpen = false;
                }
            });
            ToggleExpandedQueueCommand = new RelayCommand(() => IsExpandedQueueVisible = !IsExpandedQueueVisible);

            
            // Phase 0: Queue persistence - auto-save on changes
            Queue.CollectionChanged += OnQueueCollectionChanged;
            
            // Phase 12.6: Waveform Seeking
            eventBus.GetEvent<SeekRequestEvent>().Subscribe(evt => 
            {
                Seek((float)evt.PositionPercent);
            }).DisposeWith(_disposables);

            eventBus.GetEvent<SeekToSecondsRequestEvent>().Subscribe(evt => 
            {
                if (_playerService.Length > 0)
                {
                    Seek((float)(evt.Seconds * 1000.0 / _playerService.Length));
                }
            }).DisposeWith(_disposables);
            // Load saved queue on startup
            _ = LoadQueueAsync();
        }



        private void OnQueueCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
             if (!_suppressSave)
             {
                 DebounceSaveQueue();
             }
             OnPropertyChanged(nameof(IsQueueEmpty));
             OnPropertyChanged(nameof(ShowExpandedQueue));
             OnPropertyChanged(nameof(UpNextPreview));
             OnPropertyChanged(nameof(HasUpNext));
             ScheduleUpdateQueueStates();
        }

        private void DebounceSaveQueue()
        {
            _saveQueueTimer.Change(500, System.Threading.Timeout.Infinite);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (disposing)
            {
                _errorDismissCts?.Cancel();
                _errorDismissCts?.Dispose();
                _disposables.Dispose();
                Queue.CollectionChanged -= OnQueueCollectionChanged;
                
                // Dispose items in queue if they are IDisposable
                foreach (var item in Queue)
                {
                    item.Dispose();
                }
            }
            _isDisposed = true;
        }

        
        private void ToggleQueue()
        {
            IsQueueOpen = !IsQueueOpen;

            if (_navigationService.CurrentPage?.GetType().Name == "NowPlayingPage")
            {
                return;
            }

            if (IsQueueOpen)
            {
                _rightPanelService.OpenPanel(this, "QUEUE", "📋");
            }
            else
            {
                _rightPanelService.OpenPanel(this, "NOW PLAYING", "🎵");
            }
        }

        private void ToggleMute()
        {
            if (IsMuted)
            {
                IsMuted = false;
                Volume = _preMuteVolume > 0 ? _preMuteVolume : 100;
            }
            else
            {
                _preMuteVolume = _volume;
                IsMuted = true;
                _playerService.Volume = 0;
            }
        }

        private void OpenCurrentTrackInspector()
        {
            if (CurrentTrack == null)
            {
                PlaybackError = "Load a track before opening the inspector.";
                HasPlaybackError = true;
                return;
            }

            var selected = CurrentTrack;
            var selectedQueueIndex = CurrentQueueIndex;

            IsExpandedPlayerOpen = false;
            IsQueueOpen = false;
            _rightPanelService.OpenPanel(selected, "TRACK INSPECTOR", "🔬");
        }

        private async Task OpenPlayerViewAsync()
        {
            if (_playerNavigationInFlight)
                return;

            var now = DateTime.UtcNow;
            if (now - _lastPlayerNavigationRequestUtc < PlayerNavigationDebounceWindow)
                return;

            _playerNavigationInFlight = true;
            _lastPlayerNavigationRequestUtc = now;

            try
            {
                IsExpandedPlayerOpen = false;
                IsQueueOpen = false;
                _rightPanelService.IsPanelOpen = false;
                _navigationService.NavigateTo("Player");

                await Task.Delay(PlayerNavigationSettleDelay).ConfigureAwait(false);

                var currentPageName = _navigationService.CurrentPage?.GetType().Name;
                if (!string.Equals(currentPageName, "NowPlayingPage", StringComparison.Ordinal))
                {
                    _navigationService.NavigateTo("Home");
                }
            }
            finally
            {
                _playerNavigationInFlight = false;
            }
        }

        private void RevealCurrentTrack()
        {
            if (CurrentTrack?.RevealFileCommand?.CanExecute(null) == true)
            {
                CurrentTrack.RevealFileCommand.Execute(null);
                return;
            }

            PlaybackError = "No local file is available to reveal.";
            HasPlaybackError = true;
        }

        private void AddCurrentTrackToProject()
        {
            if (CurrentTrack?.AddToProjectCommand?.CanExecute(null) == true)
            {
                CurrentTrack.AddToProjectCommand.Execute(null);
                return;
            }

            PlaybackError = "Load a completed track before adding it to the mix.";
            HasPlaybackError = true;
        }

        private void PlayQueueItem(PlaylistTrackViewModel? track)
        {
            if (track == null)
                return;

            var index = Queue.IndexOf(track);
            if (index >= 0)
                PlayTrackAtIndex(index);
        }

        // Phase 9.3: Like Feature Implementation
        private async System.Threading.Tasks.Task ToggleLikeAsync()
        {
            if (CurrentTrack == null) return;

            // Toggle local state
            bool newLikedStatus = !IsCurrentTrackLiked;
            IsCurrentTrackLiked = newLikedStatus;

            try
            {
                // Global persistence (Library + all Project instances)
                if (global::Avalonia.Application.Current is Singularity.App app && app.Services != null)
                {
                    var libraryService = app.Services.GetService(typeof(ILibraryService)) as ILibraryService;
                    if (libraryService != null)
                    {
                        await libraryService.UpdateLikeStatusAsync(CurrentTrack.Model.TrackUniqueHash, newLikedStatus);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerViewModel] Failed to save global like status: {ex.Message}");
                // Revert on failure
                IsCurrentTrackLiked = !newLikedStatus;
            }
        }        
        // Queue Management Methods
        private void OnEndReached(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsPlaying = false;
                
                // Auto-play next track if available
                if (HasNextTrack())
                {
                    PlayNextTrack();
                }
                else if (RepeatMode == RepeatMode.All && Queue.Any())
                {
                    // Restart queue from beginning
                    CurrentQueueIndex = 0;
                    PlayTrackAtIndex(0);
                }
            });
        }
        
        public void AddToQueue(PlaylistTrackViewModel? track)
        {
            if (track == null) return;
            
            Dispatcher.UIThread.Post(() =>
            {
                Queue.Add(track);
                
                // If nothing playing, start immediately
                if (!IsPlaying && Queue.Count == 1)
                {
                    CurrentQueueIndex = 0;
                    PlayTrackAtIndex(0);
                }
            });
        }
        
        public void RemoveFromQueue(PlaylistTrackViewModel? track)
        {
            if (track == null) return;
            
            Dispatcher.UIThread.Post(() =>
            {
                var index = Queue.IndexOf(track);
                if (index >= 0)
                {
                    Queue.RemoveAt(index);
                    
                    // Adjust current index if needed
                    if (index < CurrentQueueIndex)
                    {
                        CurrentQueueIndex--;
                    }
                    else if (index == CurrentQueueIndex)
                    {
                        // Removed currently playing track
                        if (Queue.Any())
                        {
                            PlayTrackAtIndex(Math.Min(CurrentQueueIndex, Queue.Count - 1));
                        }
                        else
                        {
                            Stop();
                        }
                    }
                }
            });
        }
        
        /// <summary>
        /// Stops playback synchronously (not posted to the dispatcher) if <paramref name="globalId"/>
        /// is the track currently open — playing or paused. A caller about to permanently delete
        /// that track's file must call this and let it return BEFORE deleting: NAudio's
        /// AudioFileReader holds the file open for as long as this deck is alive, so File.Delete
        /// on a still-playing track fails outright (silently, from DownloadManager's perspective)
        /// until the deck is disposed and releases the handle. Also strips any other occurrences
        /// of the track from the queue so a deleted file can't be played again later in the session.
        /// </summary>
        public void StopIfCurrentTrack(string globalId)
        {
            if (string.IsNullOrEmpty(globalId)) return;

            if (string.Equals(CurrentTrack?.GlobalId, globalId, StringComparison.OrdinalIgnoreCase))
            {
                Stop();
                CurrentTrack = null;
            }

            foreach (var stale in Queue.Where(t => string.Equals(t.GlobalId, globalId, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                RemoveFromQueue(stale);
            }
        }

        /// <summary>
        /// User-facing "Clear all" button handler — confirms first, since ClearQueue() also stops
        /// playback and there was previously no way to back out of a misclick. Matches the
        /// existing confirm-before-destructive-action pattern elsewhere in the app (e.g.
        /// UserProfileViewModel.ClearConversationCommand). ClearQueue() itself stays
        /// confirmation-free for the one other, programmatic caller (TrackOperationsViewModel).
        /// </summary>
        private async Task ClearQueueWithConfirmationAsync()
        {
            if (_dialogService != null)
            {
                var confirmed = await _dialogService.ConfirmAsync(
                    "Clear Queue",
                    "This stops playback and removes every track from the queue. Continue?");
                if (!confirmed) return;
            }

            ClearQueue();
        }

        public void ClearQueue()
        {
            Dispatcher.UIThread.Post(() =>
            {
                Queue.Clear();
                CurrentQueueIndex = -1;
                CurrentTrack = null;
                _shuffleHistory.Clear();
                Stop();
            });
        }
        
        /// <summary>
        /// Moves a track in the queue from one position to another.
        /// Used for drag-and-drop reordering.
        /// </summary>
        public void MoveTrack(string globalId, int targetIndex)
        {
            if (string.IsNullOrEmpty(globalId) || targetIndex < 0)
                return;
                
            Dispatcher.UIThread.Post(() =>
            {
                var track = Queue.FirstOrDefault(t => t.GlobalId == globalId);
                if (track == null) return;
                    
                var oldIndex = Queue.IndexOf(track);
                if (oldIndex < 0 || oldIndex == targetIndex) return;
                    
                targetIndex = Math.Clamp(targetIndex, 0, Queue.Count - 1);
                Queue.Move(oldIndex, targetIndex);
                
                if (oldIndex == CurrentQueueIndex)
                    CurrentQueueIndex = targetIndex;
                else if (oldIndex < CurrentQueueIndex && targetIndex >= CurrentQueueIndex)
                    CurrentQueueIndex--;
                else if (oldIndex > CurrentQueueIndex && targetIndex <= CurrentQueueIndex)
                    CurrentQueueIndex++;
            });
        }
        
        private void PlayNextTrack()
        {
            if (!Queue.Any()) return;
            
            int nextIndex;
            
            if (RepeatMode == RepeatMode.One)
            {
                // Repeat current track
                nextIndex = CurrentQueueIndex;
            }
            else if (IsShuffling)
            {
                nextIndex = GetRandomTrackIndex();
                for (int attempt = 0; attempt < 10 && !IsPlayableQueueItem(nextIndex); attempt++)
                    nextIndex = GetRandomTrackIndex();
            }
            else
            {
                // Skip tracks whose file is gone — one missing file used to end playback there.
                if (NextPlayableIndex(CurrentQueueIndex + 1, wrap: RepeatMode == RepeatMode.All) is not int found)
                    return; // End of queue
                nextIndex = found;
            }

            PlayTrackAtIndex(nextIndex);
        }

        /// <summary>The queue row has a file on disk that can be played.</summary>
        private bool IsPlayableQueueItem(int index) =>
            index >= 0 && index < Queue.Count
            && Queue[index].Model?.ResolvedFilePath is { Length: > 0 } path
            && System.IO.File.Exists(path);

        /// <summary>
        /// First playable queue index at or after <paramref name="from"/> (wrapping around once when
        /// <paramref name="wrap"/>), or null. Queues are built from every track with a recorded
        /// path, and files do go missing — skipping them keeps a playlist (and its mixes) going.
        /// </summary>
        private int? NextPlayableIndex(int from, bool wrap)
        {
            int count = Queue.Count;
            for (int step = 0; step < count; step++)
            {
                int i = from + step;
                if (i >= count)
                {
                    if (!wrap) return null;
                    i -= count;
                }
                if (IsPlayableQueueItem(i))
                {
                    if (step > 0)
                        Serilog.Log.Information("[Player] Skipping {Count} queued track(s) with no playable file", step);
                    return i;
                }
            }
            return null;
        }
        
        private void PlayPreviousTrack()
        {
            if (!Queue.Any()) return;
            
            // If more than 3 seconds into track, restart current track
            if (Position > 0.05f)
            {
                Seek(0);
                return;
            }
            
            int prevIndex = CurrentQueueIndex - 1;
            if (prevIndex < 0)
            {
                if (RepeatMode == RepeatMode.All)
                {
                    prevIndex = Queue.Count - 1;
                }
                else
                {
                    return; // Start of queue
                }
            }
            
            PlayTrackAtIndex(prevIndex);
        }
        
        private void PlayTrackAtIndex(int index)
        {
            // A queued track that is missing or fails to open is skipped rather than ending
            // playback there. Bounded to one pass over the queue so a queue of nothing but broken
            // files can't loop forever.
            for (int attempts = 0; attempts < Math.Max(1, Queue.Count); attempts++)
            {
                if (index < 0 || index >= Queue.Count) return;

                var track = Queue[index];
                SetNowPlayingState(index, track);

                var filePath = track.Model?.ResolvedFilePath;
                bool loaded = false;
                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                {
                    loaded = LoadTrackCore(filePath, track.Title ?? "Unknown", track.Artist ?? "Unknown", autoPlay: true, track.Model?.Loudness);
                }
                else
                {
                    Serilog.Log.Warning("[Player] Queued track has no file on disk, skipping: {Artist} - {Title} ({Path})", track.Artist, track.Title, filePath);
                }

                if (loaded)
                {
                    SchedulePreloadNext();
                    return;
                }

                if (IsShuffling || RepeatMode == RepeatMode.One ||
                    NextPlayableIndex(index + 1, wrap: RepeatMode == RepeatMode.All) is not int next || next == index)
                {
                    return;
                }
                index = next;
            }
        }

        /// <summary>
        /// Updates queue position, current-track pointer, artwork, and like status. Shared by
        /// PlayTrackAtIndex (starting a track ourselves) and OnTrackAdvanced (the engine already
        /// advanced audio to a preloaded track and we're just syncing UI state to match).
        /// </summary>
        private void SetNowPlayingState(int index, PlaylistTrackViewModel track)
        {
            CurrentQueueIndex = index;
            CurrentTrack = track;

            // TrackTitle/TrackArtist (what the bottom player bar actually displays) are otherwise
            // only set inside LoadTrackCore — fine for PlayTrackAtIndex, which calls PlayTrack()
            // right after this, but OnTrackAdvanced (the engine autonomously promoting a deck on
            // crossfade completion) deliberately never calls PlayTrack()/LoadTrackCore — the audio
            // is already playing — so without this, the bar kept showing whatever track was
            // playing before the LAST manually-initiated play, unchanged across every automatic
            // crossfade advance for the rest of the session.
            TrackTitle = track.Title ?? "Unknown";
            TrackArtist = track.Artist ?? "Unknown";

            // Phase 9.2 & 9.3: Set album artwork and like status
            Dispatcher.UIThread.Post(async () =>
            {
                AlbumArtUrl = track.Model?.AlbumArtUrl;
                IsCurrentTrackLiked = track.Model?.IsLiked ?? false;

                // Ensure bitmap is loaded for UI
                // Phase 0: Artwork loaded via Proxy
                // if (track.ArtworkBitmap == null) await track.LoadAlbumArtworkAsync();
            });
        }

        /// <summary>Queue index the engine currently has preloaded, or null if nothing is preloaded.</summary>
        private int? _preloadedQueueIndex;

        /// <summary>
        /// Works out what track will play after the current one (without side effects) and
        /// asks the engine to have it hot and ready, so the transition can be gapless or
        /// crossfaded instead of a cold file-open. Call whenever the current track changes or
        /// anything that affects "what's next" changes (repeat mode, shuffle toggle).
        /// </summary>
        private void SchedulePreloadNext()
        {
            var nextIndex = PeekNextIndex();
            if (nextIndex is int idx && idx >= 0 && idx < Queue.Count)
            {
                var path = Queue[idx].Model?.ResolvedFilePath;
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    _playerService.PreloadNext(path, Queue[idx].Model?.Loudness);
                    _preloadedQueueIndex = idx;
                    return;
                }
            }

            _playerService.CancelPreload();
            _preloadedQueueIndex = null;
        }

        /// <summary>
        /// Determines the next queue index without mutating any state (in particular, without
        /// touching shuffle history) so it's safe to call speculatively for preloading. Shuffle
        /// mode intentionally returns null — the actual pick has side effects (recorded into
        /// shuffle history) that must only happen once, at the moment playback truly advances.
        /// </summary>
        private int? PeekNextIndex()
        {
            if (!Queue.Any()) return null;
            if (IsShuffling) return null;
            if (RepeatMode == RepeatMode.One) return CurrentQueueIndex;

            return NextPlayableIndex(CurrentQueueIndex + 1, wrap: RepeatMode == RepeatMode.All);
        }

        /// <summary>
        /// The engine autonomously advanced to the preloaded track (gapless swap or crossfade
        /// completion). Sync our "now playing" state to match — the audio is already playing,
        /// so this must NOT call PlayTrack()/Play() again.
        /// </summary>
        private void OnTrackAdvanced()
        {
            // The engine has already moved to its preloaded deck. If the preloaded index got lost
            // this used to return without updating anything: audio played the next track while
            // the UI stayed on the old one and nothing more was preloaded — so at that track's end
            // "next" was computed from the stale position and the mix chain broke.
            int? resolved = _preloadedQueueIndex is int preloaded && preloaded >= 0 && preloaded < Queue.Count
                ? preloaded
                : PeekNextIndex();
            if (resolved is not int index)
            {
                Serilog.Log.Warning("[Player] Engine advanced but the next queue track is unknown (current index {Index})", CurrentQueueIndex);
                return;
            }
            if (_preloadedQueueIndex == null)
                Serilog.Log.Warning("[Player] Engine advanced without a recorded preload — assuming queue index {Index}", index);

            var track = Queue[index];
            _preloadedQueueIndex = null;
            SetNowPlayingState(index, track);
            SchedulePreloadNext();
        }

        private bool HasNextTrack()
        {
            if (!Queue.Any()) return false;
            if (RepeatMode != RepeatMode.Off) return true;
            return CurrentQueueIndex < Queue.Count - 1;
        }
        
        private bool HasPreviousTrack()
        {
            if (!Queue.Any()) return false;
            if (RepeatMode == RepeatMode.All) return true;
            return CurrentQueueIndex > 0;
        }
        
        private int GetRandomTrackIndex()
        {
            if (Queue.Count <= 1) return 0;
            
            var random = new Random();
            int nextIndex;
            int attempts = 0;
            
            do
            {
                nextIndex = random.Next(Queue.Count);
                attempts++;
            }
            while (_shuffleHistory.Contains(nextIndex) && attempts < 10);
            
            // Track shuffle history (last 10 tracks)
            _shuffleHistory.Add(nextIndex);
            if (_shuffleHistory.Count > 10)
            {
                _shuffleHistory.RemoveAt(0);
            }
            
            return nextIndex;
        }
        
        private void ToggleShuffle()
        {
            IsShuffling = !IsShuffling;
            if (!IsShuffling)
            {
                _shuffleHistory.Clear();
            }
        }
        
        private void ToggleRepeat()
        {
            RepeatMode = RepeatMode switch
            {
                RepeatMode.Off => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                RepeatMode.One => RepeatMode.Off,
                _ => RepeatMode.Off
            };
        }

        public static string BuildTrackContextSummary(PlaylistTrackViewModel? track)
        {
            if (track == null)
                return "Load a track to inspect analysis and send it to the workstation";

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(track.BpmDisplay) && track.BpmDisplay != "—")
                parts.Add($"{track.BpmDisplay} BPM");

            if (!string.IsNullOrWhiteSpace(track.CamelotDisplay) && track.CamelotDisplay != "—")
                parts.Add(track.CamelotDisplay);

            var genre = !string.IsNullOrWhiteSpace(track.DetectedSubGenre)
                ? track.DetectedSubGenre
                : !string.IsNullOrWhiteSpace(track.Model.PrimaryGenre)
                    ? track.Model.PrimaryGenre
                    : track.Model.Genres?
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(genre))
                parts.Add(genre);

            return parts.Count == 0
                ? "Ready for playback • open the Inspector for deeper track analysis"
                : string.Join(" • ", parts.Take(3));
        }

        private void TogglePlayerDock()
        {
            // 3-state cycle for music panel button:
            // State 1: Visible + Bottom (sidepanel can be open) → State 2: Hidden
            // State 2: Hidden → State 3: Visible + RightSidebar  
            // State 3: Visible + RightSidebar → State 1: Visible + Bottom
            
            if (IsPlayerVisible && CurrentDockLocation == PlayerDockLocation.BottomBar)
            {
                // State 1 → State 2: Hide everything  
                IsPlayerVisible = false;
            }
            else if (!IsPlayerVisible)
            {
                // State 2 → State 3: Show player on right only
                IsPlayerVisible = true;
                CurrentDockLocation = PlayerDockLocation.RightSidebar;
            }
            else // IsPlayerVisible && CurrentDockLocation == RightSidebar
            {
                // State 3 → State 1: Move to bottom (allows sidepanel)
                CurrentDockLocation = PlayerDockLocation.BottomBar;
            }
        }

        private string? _currentFilePath;

        private void TogglePlayPause()
        {
            if (IsPlaying)
            {
                _playerService.Pause();
                IsPlaying = false; // Update immediate state
            }
            else
            {
                // Case 1: Track is loaded but paused/stopped
                // Check _currentFilePath (Ad-hoc play) OR CurrentTrack (Queue play)
                string? path = _currentFilePath ?? CurrentTrack?.Model?.ResolvedFilePath;
                string title = TrackTitle;
                string artist = TrackArtist;

                if (!string.IsNullOrEmpty(path))
                {
                    // Case: Track is loaded. Check if we can resume.
                    // We check if we are significantly into the track and not at the end.
                    bool canResume = _playerService.Length > 0 && _playerService.Time < _playerService.Length - 100; // 100ms buffer
                    
                    if (canResume)
                    {
                        _libraryPreviewPlayer?.StopPreview();
                        _playerService.Pause(); // Resume
                        IsPlaying = true; // Assume success
                    }
                    else
                    {
                       // Track finished or not loaded. Restart. Thread the loudness gain through
                       // like PlayTrackAtIndex does — omitting it here meant restarting a track
                       // that had already played to the end (via Play, not Next) silently dropped
                       // back to unnormalized volume for the replay.
                       PlayTrack(path!, CurrentTrack?.Title ?? "Unknown", CurrentTrack?.Artist ?? "Unknown", CurrentTrack?.Model?.Loudness);
                    }
                }
                // Case 2: No track loaded, but Queue has items
                else if (Queue.Any())
                {
                    // Start from beginning or current index
                    if (CurrentQueueIndex < 0) CurrentQueueIndex = 0;
                    PlayTrackAtIndex(CurrentQueueIndex);
                }
                
                IsPlaying = _playerService.IsPlaying;
            }
        }

        private void Stop()
        {
            _playerService.Stop();
            IsPlaying = false;
            Position = 0;
            CurrentTimeStr = "0:00";
        }
        
        // Volume Change
        private void OnVolumeChanged(int value)
        {
            if (IsMuted && value > 0)
            {
                IsMuted = false;
            }
            _playerService.Volume = IsMuted ? 0 : value;
        }

        // Seek (User Drag)
        public void Seek(float position)
        {
            _playerService.Position = position;
        }

        private void AttachCurrentTrackObservers(PlaylistTrackViewModel? previousTrack, PlaylistTrackViewModel? nextTrack)
        {
            if (previousTrack is not null)
                previousTrack.PropertyChanged -= OnCurrentTrackPropertyChanged;

            if (nextTrack is not null)
                nextTrack.PropertyChanged += OnCurrentTrackPropertyChanged;
        }

        private void OnCurrentTrackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            RaiseCurrentTrackSummaryProperties();
        }

        private void RaiseCurrentTrackSummaryProperties()
        {
            OnPropertyChanged(nameof(WaveformData));
            OnPropertyChanged(nameof(HasCurrentTrack));
            OnPropertyChanged(nameof(CurrentTrackContextSummary));
            OnPropertyChanged(nameof(CurrentTrackKeyBadge));
        }

        // Seek relative by seconds
        private void SeekRelative(double seconds)
        {
            if (_playerService.Length > 0)
            {
                double currentSeconds = _playerService.Position * _playerService.Length / 1000.0;
                double newSeconds = Math.Max(0, Math.Min(_playerService.Length / 1000.0, currentSeconds + seconds));
                double newPosition = newSeconds * 1000.0 / _playerService.Length;
                _playerService.Position = (float)newPosition;
            }
        }
        
        // Helper to load track
        public void PlayTrack(string filePath, string title, string artist, double? loudnessLufs = null)
            => _ = LoadTrackCore(filePath, title, artist, autoPlay: true, loudnessLufs);

        private bool LoadTrackCore(string filePath, string title, string artist, bool autoPlay, double? loudnessLufs = null)
        {
            Console.WriteLine($"[PlayerViewModel] LoadTrackCore called with: {filePath} (autoPlay={autoPlay})");

            // Phase 9.2: Show loading state
            Dispatcher.UIThread.Post(() =>
            {
                IsLoading = true;
                HasPlaybackError = false;
                PlaybackError = string.Empty;
            });

            try
            {
                _currentFilePath = filePath;
                TrackTitle = title;
                TrackArtist = artist;

                if (autoPlay)
                {
                    // Real playback starting — cannot have several sources racing for the audio
                    // output, so any waveform-click or hover preview stops first.
                    _libraryPreviewPlayer?.StopPreview();
                    _playerService.Play(filePath, loudnessLufs);
                }
                else _playerService.LoadWithoutPlaying(filePath, loudnessLufs);
                IsPlaying = autoPlay;

                // Hide loading state
                Dispatcher.UIThread.Post(() => IsLoading = false);
                return true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[Player] Could not play {File}", filePath);

                // Phase 9.2: Show error state with thread-safe updates
                Dispatcher.UIThread.Post(() =>
                {
                    IsLoading = false;
                    HasPlaybackError = true;
                    PlaybackError = $"Playback failed: {ex.Message}";

                    // Cancel any previous auto-dismiss, then schedule a new one
                    _errorDismissCts?.Cancel();
                    _errorDismissCts?.Dispose();
                    var cts = new System.Threading.CancellationTokenSource();
                    _errorDismissCts = cts;
                    _ = DismissErrorAfterDelayAsync(cts.Token);
                });

                IsPlaying = false;
                return false;
            }
        }

        // Phase 0: Queue Persistence Methods

        /// <summary>
        /// Auto-dismisses the playback error after 7 seconds unless cancelled.
        /// </summary>
        private async System.Threading.Tasks.Task DismissErrorAfterDelayAsync(System.Threading.CancellationToken cancellationToken)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(7000, cancellationToken).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    HasPlaybackError = false;
                    PlaybackError = string.Empty;
                });
            }
            catch (OperationCanceledException)
            {
                // Dismissed early or a new error occurred — no action needed.
            }
        }

        /// <summary>
        /// Saves the current queue to the database.
        /// </summary>
        private async System.Threading.Tasks.Task SaveQueueAsync()
        {
            try
            {
                var queueItems = Queue.Select((track, index) => (
                    trackId: track.Id,
                    position: index,
                    isCurrent: index == CurrentQueueIndex
                )).ToList();

                await _databaseService.SaveQueueAsync(queueItems);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerViewModel] Failed to save queue: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the saved queue from the database on startup.
        /// </summary>
        private async System.Threading.Tasks.Task LoadQueueAsync()
        {
            try
            {
                var savedQueue = await _databaseService.LoadQueueAsync();
                
                if (!savedQueue.Any())
                    return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // This DB read can take long enough (racing against heavy startup work like
                    // the similarity index's HNSW graph build) that the user may already have
                    // started playing something themselves — e.g. via the playlist header's Play
                    // button — before this resolves. Restoring the OLD saved queue at that point
                    // would silently wipe out the track they just started playing. Only restore
                    // into a genuinely still-empty queue.
                    if (Queue.Count > 0)
                    {
                        Console.WriteLine("[PlayerViewModel] Skipped restoring saved queue — a queue is already active");
                        return;
                    }

                    Queue.Clear();

                    // Same bug class as the PlayAlbumRequestEvent stale-path fix: a track's file
                    // can be moved/deleted between sessions without its saved ResolvedFilePath
                    // ever being corrected. Restoring it anyway meant pressing Play on a dead
                    // "now playing" entry with no error until the user actually tried — skip it
                    // instead, same as the album-play queue-build path does.
                    int currentIndex = -1;
                    int skipped = 0;
                    foreach (var (track, isCurrent) in savedQueue)
                    {
                        if (string.IsNullOrEmpty(track.ResolvedFilePath) || !System.IO.File.Exists(track.ResolvedFilePath))
                        {
                            skipped++;
                            continue;
                        }

                        var vm = new PlaylistTrackViewModel(track);
                        Queue.Add(vm);

                        if (isCurrent)
                            currentIndex = Queue.Count - 1;
                    }

                    // Restore current track position
                    if (currentIndex >= 0 && currentIndex < Queue.Count)
                    {
                        CurrentQueueIndex = currentIndex;
                        CurrentTrack = Queue[currentIndex];
                    }

                    Console.WriteLine($"[PlayerViewModel] Loaded {Queue.Count} tracks from saved queue" + (skipped > 0 ? $" ({skipped} skipped — file no longer exists)" : ""));
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerViewModel] Failed to load queue: {ex.Message}");
            }
        }

        // Drag & Drop
        // TODO: Fix drag-drop library reference
        /*
        public DraggingServiceDropEvent OnDropQueue => (DraggingServiceDropEventsArgs args) => {
            var droppedTracks = DragContext.Current as List<PlaylistTrackViewModel>;
            if (droppedTracks != null && droppedTracks.Any())
            {
                Dispatcher.UIThread.Post(() => {
                    foreach (var track in droppedTracks)
                    {
                        AddToQueue(track);
                    }
                });
            }
        };
        */
    }
}
