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
            if (e.Key == Key.Escape || (e.Key == Key.Enter && vm.ShowResults)) vm.BackCommand.Execute(null);
            else if (e.Key == Key.R && vm.ShowResults) vm.RestartCommand.Execute(null);
            else if ((e.Key is Key.Space or Key.P) && !vm.ShowResults) vm.PauseCommand.Execute(null);
            else if (e.Key == Key.V) vm.CycleVocalsCommand.Execute(null);
            else if (e.Key == Key.S && !vm.ShowResults) vm.SkipIntroCommand.Execute(null);
            else if (e.Key == Key.N && vm.IsJukebox) vm.NextJukeboxCommand.Execute(null);
            else if (e.Key is Key.OemPlus or Key.Add) vm.BiggerTextCommand.Execute(null);
            else if (e.Key is Key.OemMinus or Key.Subtract) vm.SmallerTextCommand.Execute(null);
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
