using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Singularity.ViewModels;
using Singularity.Models;

namespace Singularity.Views.Avalonia.Controls;

public partial class LibraryTrackInspector : UserControl
{
    public LibraryTrackInspector()
    {
        InitializeComponent();
        // Trigger analysis data loading when the inspector becomes visible
        this.DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is PlaylistTrackViewModel vm)
            await vm.LoadAnalysisDataAsync();
    }

}
