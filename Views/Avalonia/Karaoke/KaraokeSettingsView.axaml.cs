using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Singularity.ViewModels.Karaoke;

namespace Singularity.Views.Avalonia.Karaoke;

public partial class KaraokeSettingsView : UserControl
{
    public KaraokeSettingsView() => InitializeComponent();

    private async void OnAddSongFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is KaraokeSettingsViewModel vm && await PickFolderAsync("Add a folder with UltraStar songs") is { } folder)
            vm.AddFolder(folder);
    }

    private async void OnChangeIngestFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is KaraokeSettingsViewModel vm && await PickFolderAsync("Where new songs are made") is { } folder)
            vm.IngestFolder = folder;
    }

    private async void OnChooseInferenceFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is KaraokeSettingsViewModel vm && await PickFolderAsync("The inference folder (with .venv inside)") is { } folder)
            vm.ChooseInferenceFolder(folder);
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }
}
