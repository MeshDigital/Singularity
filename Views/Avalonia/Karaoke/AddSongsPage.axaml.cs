using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Singularity.ViewModels.Karaoke;

namespace Singularity.Views.Avalonia.Karaoke;

public partial class AddSongsPage : UserControl
{
    public AddSongsPage()
    {
        InitializeComponent();
        // The box takes lists, so it accepts new lines; plain Enter still adds (Shift+Enter is a new line).
        Input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            if (DataContext is AddSongsViewModel vm && vm.ImportCommand.CanExecute(null)) vm.ImportCommand.Execute(null);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    public AddSongsPage(AddSongsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private async void OnAddFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddSongsViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Make songs from audio files",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.flac", "*.m4a", "*.ogg", "*.opus", "*.wav" } } },
        });
        foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>())
            vm.AddFile(path);
    }
}
