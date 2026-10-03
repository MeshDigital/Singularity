using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Singularity.ViewModels.Karaoke;

namespace Singularity.Views.Avalonia.Karaoke;

public partial class SongSelectPage : UserControl
{
    public SongSelectPage()
    {
        InitializeComponent();
        SongList.DoubleTapped += (_, _) => SingSelected();
        SongList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) SingSelected();
        };
    }

    public SongSelectPage(SongSelectViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is SongSelectViewModel vm) await vm.EnsureLoadedAsync();
    }

    private void SingSelected()
    {
        if (DataContext is SongSelectViewModel vm && SongList.SelectedItem is SongCardViewModel card && vm.SingCommand.CanExecute(card))
            vm.SingCommand.Execute(card);
    }
}
