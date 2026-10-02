using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using Singularity.Services;

namespace Singularity.ViewModels
{
    // Order matches the visual TabItem order in MainWindow.axaml's right-panel TabControl —
    // ActiveTabIndex below maps directly to this ordinal, driving TabControl.SelectedIndex.
    public enum SidebarTab { Inspector, Player, Notifications }

    public class SidebarViewModel : ReactiveObject, IDisposable
    {
        private readonly IRightPanelService _rightPanelService;
        private readonly CompositeDisposable _disposables = new();
        private object? _lastInspectorContent;

        private SidebarTab _activeTab = SidebarTab.Inspector;
        public SidebarTab ActiveTab
        {
            get => _activeTab;
            set
            {
                this.RaiseAndSetIfChanged(ref _activeTab, value);
                this.RaisePropertyChanged(nameof(IsPlayerTab));
                this.RaisePropertyChanged(nameof(IsInspectorTab));
                this.RaisePropertyChanged(nameof(IsNotificationsTab));
                this.RaisePropertyChanged(nameof(ActiveTabIndex));
            }
        }

        public bool IsPlayerTab        => ActiveTab == SidebarTab.Player;
        public bool IsInspectorTab     => ActiveTab == SidebarTab.Inspector;
        public bool IsNotificationsTab => ActiveTab == SidebarTab.Notifications;

        // TabControl.SelectedIndex has no enum overload — this is the int-typed mirror of
        // ActiveTab that the XAML actually binds to (two-way, so a manual tab click updates
        // ActiveTab too).
        public int ActiveTabIndex
        {
            get => (int)ActiveTab;
            set
            {
                var newTab = Enum.IsDefined(typeof(SidebarTab), value) ? (SidebarTab)value : SidebarTab.Inspector;
                if (newTab != ActiveTab) ActiveTab = newTab;
            }
        }

        // Tab switch commands
        public ReactiveCommand<Unit, Unit> SwitchToPlayerCommand     { get; }
        public ReactiveCommand<Unit, Unit> SwitchToInspectorCommand  { get; }

        // Close command (ICommand — bindable in AXAML)
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }

        // Notifications (bell/side panel)
        public ReactiveCommand<Unit, Unit> OpenNotificationsCommand { get; }

        public PlayerViewModel PlayerVm { get; }
        public NotificationCenterService NotificationCenter { get; }

        public SidebarViewModel(
            IRightPanelService rightPanelService,
            PlayerViewModel playerVm,
            NotificationCenterService notificationCenter)
        {
            _rightPanelService = rightPanelService;
            PlayerVm           = playerVm;
            NotificationCenter = notificationCenter;

            // Mirror RightPanelService reactive properties
            this.WhenAnyValue(x => x._rightPanelService.CurrentPanelVm)
                .Subscribe(vm =>
                {
                    this.RaisePropertyChanged(nameof(CurrentContent));

                    if (vm is PlaylistTrackViewModel playlistTrack)
                        _ = playlistTrack.LoadAnalysisDataAsync();

                    if (vm is PlayerViewModel)
                    {
                        ActiveTab = SidebarTab.Player;
                    }
                    else if (vm is NotificationCenterService)
                    {
                        ActiveTab = SidebarTab.Notifications;
                    }
                    else if (vm != null)
                    {
                        _lastInspectorContent = vm;
                        ActiveTab = SidebarTab.Inspector;
                    }
                })
                .DisposeWith(_disposables);
            this.WhenAnyValue(x => x._rightPanelService.IsPanelOpen)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(IsVisible)))
                .DisposeWith(_disposables);
            this.WhenAnyValue(x => x._rightPanelService.ModeLabel)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(ModeLabel)))
                .DisposeWith(_disposables);
            this.WhenAnyValue(x => x._rightPanelService.ModeIcon)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(ModeIcon)))
                .DisposeWith(_disposables);

            SwitchToPlayerCommand     = ReactiveCommand.Create(() =>
            {
                ActiveTab = SidebarTab.Player;
                _rightPanelService.OpenPanel(PlayerVm, "NOW PLAYING", "🎵");
            });
            SwitchToInspectorCommand  = ReactiveCommand.Create(() =>
            {
                ActiveTab = SidebarTab.Inspector;

                if (_lastInspectorContent != null)
                {
                    _rightPanelService.OpenPanel(_lastInspectorContent, "TRACK INSPECTOR", "🔬");
                }
                else
                {
                    _rightPanelService.IsPanelOpen = true;
                }
            });
            CloseCommand = ReactiveCommand.Create(() => _rightPanelService.ClosePanel());

            OpenNotificationsCommand = ReactiveCommand.Create(() =>
            {
                _rightPanelService.OpenPanel(NotificationCenter, "NOTIFICATIONS", "🔔");
            });
        }

        public object? CurrentContent => _rightPanelService.CurrentPanelVm;
        public bool IsVisible => _rightPanelService.IsPanelOpen;
        public string? ModeLabel => _rightPanelService.ModeLabel;
        public string? ModeIcon => _rightPanelService.ModeIcon;

        /// <summary>Kept for backwards-compat with any code-behind callers.</summary>
        public void Close() => _rightPanelService.ClosePanel();

        public void Dispose() => _disposables.Dispose();
    }
}
