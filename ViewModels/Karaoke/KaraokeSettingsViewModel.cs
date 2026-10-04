using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using ReactiveUI;
using Singularity.Configuration;
using Singularity.Services;
using Singularity.Services.Inference;
using Singularity.Services.Karaoke;
using Singularity.Services.Karaoke.Ingest;
using Singularity.Services.Karaoke.Usdb;
using Singularity.Views;

namespace Singularity.ViewModels.Karaoke;

/// <summary>
/// Settings for a karaoke setup, on one page: the accounts songs come from (Soulseek, Spotify, USDB),
/// the folders, how songs are added, singing defaults and whether the AI tools are there. Connection
/// state and commands come from ORBIT's settings (<see cref="Orbit"/>), whose full page stays one
/// switch away (<see cref="ShowAdvanced"/>).
/// </summary>
public sealed class KaraokeSettingsViewModel : ReactiveObject
{
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly KaraokeLibrary _library;
    private readonly SingViewModel _sing;
    private readonly INavigationService _navigation;
    private bool _showAdvanced;
    private string _usdbUser = "";
    private string _usdbPassword = "";
    private string _usdbStatus = "";

    public KaraokeSettingsViewModel(SettingsViewModel orbit, AppConfig config, ConfigManager configManager, KaraokeLibrary library,
        SingViewModel sing, INavigationService navigation)
    {
        Orbit = orbit;
        _config = config;
        InferenceWorkerOptions.ChosenDirectory = string.IsNullOrWhiteSpace(config.KaraokeInferenceFolder) ? null : config.KaraokeInferenceFolder;
        _configManager = configManager;
        _library = library;
        _sing = sing;
        _navigation = navigation;
        foreach (var folder in ConfiguredFolders()) SongFolders.Add(folder);
        RemoveFolderCommand = new RelayCommand<string>(RemoveFolder);
        SaveUsdbCommand = new RelayCommand(SaveUsdb, () => UsdbUser.Trim().Length > 0 && UsdbPassword.Length > 0);
        OpenMicrophoneCommand = new RelayCommand(() => _navigation.NavigateTo("MicSetup"));
        var saved = UsdbCredentials.Load();
        _usdbUser = saved?.User ?? "";
        _usdbStatus = saved is null ? "No account saved: songs get AI charts." : $"Saved for {saved.Value.User}.";
    }

    /// <summary>ORBIT's settings: Soulseek and Spotify connection, download folder.</summary>
    public SettingsViewModel Orbit { get; }

    /// <summary>Shows ORBIT's full settings instead.</summary>
    public bool ShowAdvanced { get => _showAdvanced; set => this.RaiseAndSetIfChanged(ref _showAdvanced, value); }

    // ── Folders ────────────────────────────────────────────────────────────

    /// <summary>The user's UltraStar song folders (read only; Singularity never writes there).</summary>
    public ObservableCollection<string> SongFolders { get; } = new();

    public bool UsesDefaultFolder => string.IsNullOrWhiteSpace(_config.KaraokeSongFolders);

    public ICommand RemoveFolderCommand { get; }

    public void AddFolder(string folder)
    {
        if (SongFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
        if (UsesDefaultFolder) SongFolders.Clear(); // the found-by-itself folder becomes an explicit choice
        SongFolders.Add(folder);
        SaveFolders();
    }

    private void RemoveFolder(string? folder)
    {
        if (folder is null) return;
        SongFolders.Remove(folder);
        SaveFolders();
    }

    private void SaveFolders()
    {
        _config.KaraokeSongFolders = string.Join(";", SongFolders);
        Save();
        this.RaisePropertyChanged(nameof(UsesDefaultFolder));
        _library.NotifySongsAdded(); // rescan
    }

    private string[] ConfiguredFolders() => UsesDefaultFolder
        ? (Directory.Exists(KaraokeLibrary.DefaultCollectionFolder) ? new[] { KaraokeLibrary.DefaultCollectionFolder } : Array.Empty<string>())
        : _config.KaraokeSongFolders.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Where songs Singularity makes go.</summary>
    public string IngestFolder
    {
        get => _library.IngestFolder;
        set
        {
            _config.KaraokeIngestFolder = value?.Trim() ?? "";
            Save();
            this.RaisePropertyChanged();
            _library.NotifySongsAdded();
        }
    }

    // ── Adding songs ───────────────────────────────────────────────────────

    public bool IngestAllDownloads { get => _config.KaraokeIngestAllDownloads; set => Set(v => _config.KaraokeIngestAllDownloads = v, value); }
    public bool UseCommunityCharts { get => _config.KaraokeUseCommunityCharts; set => Set(v => _config.KaraokeUseCommunityCharts = v, value); }
    public bool DownloadVideos { get => _config.KaraokeDownloadVideos; set => Set(v => _config.KaraokeDownloadVideos = v, value); }

    // ── USDB account ───────────────────────────────────────────────────────

    public string UsdbUser
    {
        get => _usdbUser;
        set
        {
            this.RaiseAndSetIfChanged(ref _usdbUser, value);
            (SaveUsdbCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Typed only to save it; the saved password is never shown again.</summary>
    public string UsdbPassword
    {
        get => _usdbPassword;
        set
        {
            this.RaiseAndSetIfChanged(ref _usdbPassword, value);
            (SaveUsdbCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string UsdbStatus { get => _usdbStatus; private set => this.RaiseAndSetIfChanged(ref _usdbStatus, value); }
    public ICommand SaveUsdbCommand { get; }

    private void SaveUsdb()
    {
        try
        {
            UsdbCredentials.Save(UsdbUser.Trim(), UsdbPassword);
            UsdbPassword = "";
            UsdbStatus = $"Saved for {UsdbUser.Trim()}. It's checked on the next song added.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            UsdbStatus = "Couldn't save it: " + ex.Message;
        }
    }

    // ── Singing ────────────────────────────────────────────────────────────

    public string[] VocalsOptions { get; } = { "Off", "Guide", "Full" };

    public string[] Difficulties { get; } = { "Easy", "Medium", "Hard" };

    /// <summary>Names on the high scores.</summary>
    public string Player1Name { get => _config.KaraokePlayer1Name; set => Set(v => _config.KaraokePlayer1Name = string.IsNullOrWhiteSpace(v) ? "Player 1" : v.Trim(), value); }
    public string Player2Name { get => _config.KaraokePlayer2Name; set => Set(v => _config.KaraokePlayer2Name = string.IsNullOrWhiteSpace(v) ? "Player 2" : v.Trim(), value); }

    /// <summary>How close singing must be to count (also on the Sing page).</summary>
    public string Difficulty
    {
        get => _sing.Difficulty.ToString();
        set
        {
            if (Enum.TryParse<Singularity.Karaoke.Scoring.Difficulty>(value, out var d)) _sing.Difficulty = d;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>The original vocals while singing a song with separated stems: off, quiet guide, or full.</summary>
    public string Vocals
    {
        get => _config.KaraokeVocals is "Guide" or "Full" ? _config.KaraokeVocals : "Off";
        set => Set(v => _config.KaraokeVocals = v, value ?? "Off");
    }

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

    public ICommand OpenMicrophoneCommand { get; }

    // ── System ─────────────────────────────────────────────────────────────

    public string AiStatus => InferenceWorkerOptions.Discover() is not null
        ? "Installed: makes charts and removes vocals"
        : "Not found: no AI charts or vocal removal. Set up inference/ (see its README), then choose its folder.";

    public bool AiInstalled => InferenceWorkerOptions.Discover() is not null;

    /// <summary>Points Singularity at an AI worker folder (one holding .venv); false when there's no worker in it.</summary>
    public bool ChooseInferenceFolder(string folder)
    {
        var previous = InferenceWorkerOptions.ChosenDirectory;
        InferenceWorkerOptions.ChosenDirectory = folder;
        if (InferenceWorkerOptions.Discover() is null)
        {
            InferenceWorkerOptions.ChosenDirectory = previous;
            return false;
        }
        _config.KaraokeInferenceFolder = folder;
        Save();
        this.RaisePropertyChanged(nameof(AiStatus));
        this.RaisePropertyChanged(nameof(AiInstalled));
        return true;
    }

    public string VideoToolStatus => YtDlpVideoFinder.Locate() is { } path
        ? "yt-dlp installed: music videos are found"
        : "yt-dlp not installed: songs get their cover instead of a video";

    public bool VideoToolInstalled => YtDlpVideoFinder.Locate() is not null;

    private void Set<T>(Action<T> apply, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        apply(value);
        Save();
        this.RaisePropertyChanged(name);
    }

    private void Save() => _ = _configManager.SaveAsync(_config);
}
