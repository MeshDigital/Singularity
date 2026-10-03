using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using Singularity.Configuration;

namespace Singularity.Services.Karaoke;

/// <summary>A place the sing stage can be shown: the main window, or one of the connected displays.</summary>
public sealed record StageScreenOption(string Key, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Puts the sing stage on a second display (projector, TV) as a borderless full-screen window, so
/// the main window stays free for the library and song select. Displays are identified by their
/// position on the desktop ("x,y"), which stays the same while the display arrangement does.
/// </summary>
public sealed class StageScreenService
{
    public const string MainWindowKey = "";

    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly ILogger<StageScreenService> _logger;
    private Window? _window;

    public StageScreenService(AppConfig config, ConfigManager configManager, ILogger<StageScreenService> logger)
    {
        _config = config;
        _configManager = configManager;
        _logger = logger;
    }

    public string SelectedKey
    {
        get => RuntimeOptions.StageScreen ?? _config.KaraokeStageScreen ?? MainWindowKey;
        set
        {
            _config.KaraokeStageScreen = value ?? MainWindowKey;
            _ = _configManager.SaveAsync(_config);
        }
    }

    public IReadOnlyList<StageScreenOption> Options()
    {
        var options = new List<StageScreenOption> { new(MainWindowKey, "This window") };
        var screens = Screens();
        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            string name = string.IsNullOrWhiteSpace(s.DisplayName) ? $"Screen {i + 1}" : s.DisplayName!;
            options.Add(new StageScreenOption(KeyOf(s), $"{name} ({s.Bounds.Width}×{s.Bounds.Height}){(s.IsPrimary ? ", main display" : "")}"));
        }
        return options;
    }

    /// <summary>Opens the stage on the chosen display. Returns the display's name, or null to sing in the main window.</summary>
    public string? Open(object dataContext)
    {
        Close();
        if (SelectedKey == MainWindowKey) return null;
        var screen = Screens().FirstOrDefault(s => KeyOf(s) == SelectedKey);
        if (screen is null)
        {
            _logger.LogWarning("Stage display {Key} is not connected; singing in the main window", SelectedKey);
            return null;
        }

        var window = new Views.Avalonia.Karaoke.StageWindow { DataContext = dataContext, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Position = screen.Bounds.Position;
        window.Width = screen.Bounds.Width / screen.Scaling;
        window.Height = screen.Bounds.Height / screen.Scaling;
        window.Show();
        window.WindowState = WindowState.FullScreen;
        _window = window;
        _logger.LogInformation("Stage shown full screen on {Screen}", screen.DisplayName ?? SelectedKey);
        return screen.DisplayName ?? "the second screen";
    }

    public void Close()
    {
        var window = _window;
        _window = null;
        window?.Close();
    }

    private static string KeyOf(Screen screen) => $"{screen.Bounds.X},{screen.Bounds.Y}";

    private static IReadOnlyList<Screen> Screens() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Screens.All
        ?? (IReadOnlyList<Screen>)Array.Empty<Screen>();
}
