using Avalonia.Controls;
using Singularity.ViewModels.Karaoke;

namespace Singularity.Views.Avalonia.Karaoke;

public partial class ChartEditorPage : UserControl
{
    public ChartEditorPage()
    {
        InitializeComponent();
        LineView.NoteClicked += index => (DataContext as ChartEditorViewModel)?.SelectNote(index);
    }

    public ChartEditorPage(ChartEditorViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
