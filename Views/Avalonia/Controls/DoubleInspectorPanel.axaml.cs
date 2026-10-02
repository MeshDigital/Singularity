using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Singularity.Views.Avalonia.Controls;

public partial class DoubleInspectorPanel : UserControl
{
    public DoubleInspectorPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}