using Avalonia.Controls;
using Avalonia.Input;
using Singularity.ViewModels.Karaoke;

namespace Singularity.Views.Avalonia.Karaoke;

public partial class StageWindow : Window
{
    public StageWindow()
    {
        InitializeComponent();
        // The same keys as the sing page, for whoever is standing at the projector's keyboard.
        KeyDown += (_, e) =>
        {
            if (DataContext is StageBrowseViewModel browse)
            {
                // Song select from across the room: songs left and right, a song's versions up and down.
                if (e.Key == Key.Left) browse.MoveSong(-1);
                else if (e.Key == Key.Right) browse.MoveSong(+1);
                else if (e.Key == Key.PageUp) browse.MoveSong(-10);
                else if (e.Key == Key.PageDown) browse.MoveSong(+10);
                else if (e.Key == Key.Up) browse.MoveVersion(-1);
                else if (e.Key == Key.Down) browse.MoveVersion(+1);
                else if (e.Key == Key.Enter) browse.Sing();
                else return;
                e.Handled = true;
                return;
            }
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
}
