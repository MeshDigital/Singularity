using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Singularity.Views.Avalonia
{
    public partial class MixTransitionPanel : UserControl
    {
        public MixTransitionPanel()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
