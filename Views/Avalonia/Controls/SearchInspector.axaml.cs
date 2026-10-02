using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Singularity.Views.Avalonia.Controls;

public partial class SearchInspector : UserControl
{
    public SearchInspector()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
