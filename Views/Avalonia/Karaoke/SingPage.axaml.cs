using Avalonia.Controls;
using Avalonia.Input;
using Singularity.ViewModels.Karaoke;

namespace Singularity.Views.Avalonia.Karaoke;

public partial class SingPage : UserControl
{
    public SingPage()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (DataContext is not SingViewModel vm) return;
            if (e.Key == Key.Escape) vm.BackCommand.Execute(null);
            else if (e.Key is Key.Space or Key.P) vm.PauseCommand.Execute(null);
            else return;
            e.Handled = true;
        };
    }

    public SingPage(SingViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Focus();
    }
}
