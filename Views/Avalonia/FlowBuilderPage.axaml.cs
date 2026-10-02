using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Singularity.Views.Avalonia;

public partial class FlowBuilderPage : UserControl
{
    public FlowBuilderPage()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
