using Avalonia;
using Avalonia.Controls;
using Singularity.ViewModels.Karaoke;

namespace Singularity.Views.Avalonia.Karaoke;

public partial class MicSetupPage : UserControl
{
    public MicSetupPage() => InitializeComponent();

    public MicSetupPage(MicSetupViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    // The microphone is only held while the page is on screen.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as MicSetupViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as MicSetupViewModel)?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }
}
