using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Singularity.ViewModels;

namespace Singularity.Views.Avalonia;

public partial class LibrarySourcesView : UserControl
{
    public LibrarySourcesView()
    {
        InitializeComponent();
    }
    
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
