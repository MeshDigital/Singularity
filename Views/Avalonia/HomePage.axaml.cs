using Avalonia.Controls;
using System;
using System.Globalization;

namespace Singularity.Views.Avalonia;

/// <summary>
/// Phase 6D: Home page with dashboard stats and quick actions.
/// </summary>
public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
    }

    public HomePage(Singularity.ViewModels.HomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

