using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Editing;
using Singularity.Karaoke.Scoring;

namespace Singularity.Views.Avalonia.Karaoke;

/// <summary>What the line view draws: one line of a chart, the original singer's pitch, and where playback is.</summary>
/// <param name="SelectedNote">Index into the voice's notes, or -1.</param>
/// <param name="PlayMs">Song position while the line plays; null when stopped.</param>
public sealed record ChartLineFrame(UltraStarSong Song, int Voice, ChartLine Line, ReferencePitch? Singer, int SelectedNote, double? PlayMs);

/// <summary>
/// One line of a chart for correcting it: its notes on a pitch grid (a row per semitone, note names on C), the
/// syllables under them, and the original singer's pitch over them as a curve, folded to the octave of the nearest
/// note, so a note in the wrong place shows as notes and curve apart. Clicking a note selects it.
/// </summary>
public sealed class ChartLineView : Control
{
    public static readonly StyledProperty<ChartLineFrame?> FrameProperty =
        AvaloniaProperty.Register<ChartLineView, ChartLineFrame?>(nameof(Frame));

    static ChartLineView() => AffectsRender<ChartLineView>(FrameProperty);

    public ChartLineFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>Raised with the voice's note index when a note is clicked.</summary>
    public event Action<int>? NoteClicked;

    private static readonly IBrush Background = new SolidColorBrush(Color.FromArgb(255, 18, 20, 30));
    private static readonly IPen Row = new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 1);
    private static readonly IPen RowC = new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 1);
    private static readonly IBrush NoteBrush = new SolidColorBrush(Color.FromArgb(200, 200, 210, 230));
    private static readonly IBrush GoldenBrush = new SolidColorBrush(Color.FromRgb(255, 210, 60));
    private static readonly IPen Selected = new Pen(new SolidColorBrush(Color.FromRgb(180, 140, 240)), 3);
    private static readonly IPen Freestyle = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 1.5, DashStyle.Dash);
    private static readonly IBrush SingerBrush = new SolidColorBrush(Color.FromRgb(70, 225, 235));
    private static readonly IBrush Label = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    private static readonly IBrush Text = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
    private static readonly IPen Playhead = new Pen(Brushes.White, 2);
    private static readonly Typeface Font = new(FontFamily.Default);

    private const double Margin = 48;

    private (double FromBeat, double ToBeat, double Low, double High) Window(ChartLineFrame f)
    {
        var notes = f.Song.Voices[f.Voice].Notes;
        var line = Enumerable.Range(f.Line.From, f.Line.Count).Select(i => notes[i]).ToList();
        int low = line.Min(n => n.MidiTone), high = line.Max(n => n.MidiTone);
        double pad = Math.Max(4, (14 - (high - low)) / 2.0);
        return (line.Min(n => n.StartBeat) - 2, line.Max(n => n.StartBeat + n.DurationBeats) + 2, low - pad, high + pad);
    }

    private (Func<double, double> X, Func<double, double> Y) Map(ChartLineFrame f)
    {
        var (from, to, low, high) = Window(f);
        double w = Bounds.Width - Margin - 12, h = Bounds.Height - 60;
        return (beat => Margin + (beat - from) / (to - from) * w, midi => 10 + (high - midi) / (high - low) * h);
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Background, null, new Rect(Bounds.Size), 12, 12);
        if (Frame is not { } f || f.Line.Count == 0) return;
        var (from, to, low, high) = Window(f);
        var (X, Y) = Map(f);
        var notes = f.Song.Voices[f.Voice].Notes;

        // Semitone rows, with note names on the Cs.
        for (int m = (int)Math.Ceiling(low); m <= high; m++)
        {
            bool c = ((m % 12) + 12) % 12 == 0;
            ctx.DrawLine(c ? RowC : Row, new Point(Margin, Y(m)), new Point(Bounds.Width - 12, Y(m)));
            if (c) ctx.DrawText(new FormattedText(Singularity.Karaoke.Pitch.NoteNames.Name(m), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 12, Label), new Point(8, Y(m) - 8));
        }
        double rowH = Math.Max(6, Y(0) - Y(1));

        for (int i = f.Line.From; i < f.Line.To; i++)
        {
            var n = notes[i];
            var rect = new Rect(X(n.StartBeat), Y(n.MidiTone) - rowH / 2, Math.Max(4, X(n.StartBeat + n.DurationBeats) - X(n.StartBeat) - 2), rowH);
            if (n.Type == NoteType.Freestyle) ctx.DrawRectangle(null, Freestyle, rect, 4, 4);
            else ctx.DrawRectangle(n.IsGolden ? GoldenBrush : NoteBrush, i == f.SelectedNote ? Selected : null, rect, 4, 4);
            if (i == f.SelectedNote && n.Type == NoteType.Freestyle) ctx.DrawRectangle(null, Selected, rect, 4, 4);
            ctx.DrawText(new FormattedText(n.Syllable, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 13, Text),
                new Point(rect.X, Bounds.Height - 42));
        }

        // The original singer, folded to the octave of the note nearest in time.
        if (f.Singer is { } singer)
        {
            double fromMs = f.Song.BeatToMs(from), toMs = f.Song.BeatToMs(to);
            for (double ms = fromMs; ms < toMs; ms += 10)
            {
                if (singer.At(ms) is not { } midi) continue;
                double beat = f.Song.MsToBeat(ms);
                var near = Enumerable.Range(f.Line.From, f.Line.Count).Select(i => notes[i])
                    .OrderBy(n => beat < n.StartBeat ? n.StartBeat - beat : beat > n.StartBeat + n.DurationBeats ? beat - n.StartBeat - n.DurationBeats : 0)
                    .First();
                double folded = midi + 12 * Math.Round((near.MidiTone - midi) / 12);
                if (folded < low || folded > high) continue;
                ctx.DrawEllipse(SingerBrush, null, new Point(X(beat), Y(folded)), 2.2, 2.2);
            }
        }

        if (f.PlayMs is { } play)
        {
            double x = X(f.Song.MsToBeat(play));
            if (x >= Margin && x <= Bounds.Width - 12) ctx.DrawLine(Playhead, new Point(x, 6), new Point(x, Bounds.Height - 50));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Frame is not { } f) return;
        var (X, Y) = Map(f);
        var at = e.GetPosition(this);
        var notes = f.Song.Voices[f.Voice].Notes;
        double rowH = Math.Max(6, Y(0) - Y(1));
        for (int i = f.Line.From; i < f.Line.To; i++)
        {
            var n = notes[i];
            var rect = new Rect(X(n.StartBeat), Y(n.MidiTone) - rowH, Math.Max(8, X(n.StartBeat + n.DurationBeats) - X(n.StartBeat)), rowH * 2);
            if (rect.Contains(at))
            {
                NoteClicked?.Invoke(i);
                return;
            }
        }
    }
}
