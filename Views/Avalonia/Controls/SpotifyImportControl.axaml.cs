using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Singularity.Views.Avalonia.Controls;

public partial class SpotifyImportControl : UserControl
{
    public SpotifyImportControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
