using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Singularity.Karaoke.Pitch;
using Singularity.ViewModels.Karaoke;

namespace Singularity.Views.Avalonia.Karaoke;

/// <summary>
/// The last few seconds of detected pitch as a scrolling trace, with note-name gridlines: shows at a
/// glance whether the pitch holds steady, wobbles, or jumps octaves. Newest on the right.
/// </summary>
public sealed class PitchTrail : Control
{
    private static readonly IBrush Background = new SolidColorBrush(Color.FromArgb(160, 10, 12, 20));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
    private static readonly IBrush Dot = new SolidColorBrush(Color.FromRgb(60, 170, 255));
    private static readonly IBrush Label = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
    private static readonly Typeface Font = new(FontFamily.Default);

    private MicSetupViewModel? _vm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.Refreshed -= InvalidateVisual;
        _vm = DataContext as MicSetupViewModel;
        if (_vm is not null) _vm.Refreshed += InvalidateVisual;
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.DrawRectangle(Background, null, bounds, 8, 8);
        if (_vm is null) return;

        var trail = _vm.Trail;
        var voiced = trail.Where(t => t.Midi is not null).Select(t => t.Midi!.Value).ToList();
        // Centre the view on the recent singing, one octave either side; default around A3.
        double centre = voiced.Count > 0 ? voiced.OrderBy(m => m).ElementAt(voiced.Count / 2) : 57;
        double low = Math.Round(centre) - 12, high = Math.Round(centre) + 12;
        double Y(double midi) => bounds.Height - 8 - (midi - low) / (high - low) * (bounds.Height - 16);
        double X(double secondsAgo) => bounds.Width - 8 - secondsAgo / MicSetupViewModel.TrailSeconds * (bounds.Width - 48);

        for (int m = (int)Math.Ceiling(low); m <= high; m++)
        {
            if (((m % 12) + 12) % 12 is not (0 or 9)) continue; // gridlines on C and A
            ctx.DrawLine(GridPen, new Point(40, Y(m)), new Point(bounds.Width - 8, Y(m)));
            ctx.DrawText(new FormattedText(NoteNames.Name(m), System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Font, 11, Label), new Point(6, Y(m) - 8));
        }

        foreach (var (secondsAgo, midi) in trail)
        {
            if (midi is not { } value || value < low || value > high) continue;
            ctx.DrawEllipse(Dot, null, new Point(X(secondsAgo), Y(value)), 2.5, 2.5);
        }
    }
}
