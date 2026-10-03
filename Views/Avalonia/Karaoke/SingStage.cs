using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Display;
using Singularity.Karaoke.Scoring;
using Singularity.ViewModels.Karaoke;

namespace Singularity.Views.Avalonia.Karaoke;

/// <summary>
/// Draws the sing screen: note lane, hit marks, beat cursor, the singer's pitch, lyrics with their
/// colour wipe, countdown, score and line ratings. It redraws once per display refresh
/// (TopLevel.RequestAnimationFrame) and asks the view model for a snapshot each time. Every position
/// derives from the audio device clock in that snapshot, so a late frame shows the right moment
/// instead of drifting.
/// </summary>
public sealed class SingStage : Control
{
    private static readonly Typeface Font = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IBrush LaneBrush = new SolidColorBrush(Color.FromArgb(150, 10, 12, 20));
    private static readonly IBrush NoteBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
    private static readonly IBrush GoldenBrush = new SolidColorBrush(Color.FromArgb(150, 255, 200, 40));
    private static readonly IBrush HitBrush = new SolidColorBrush(Color.FromRgb(60, 170, 255));
    private static readonly IBrush GoldenHitBrush = new SolidColorBrush(Color.FromRgb(255, 210, 60));
    private static readonly IPen FreestylePen = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1.5, DashStyle.Dash);
    private static readonly IPen CursorPen = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 2);
    private static readonly IBrush PitchBrush = new SolidColorBrush(Color.FromRgb(255, 90, 120));
    private static readonly IBrush Sung = new SolidColorBrush(Color.FromRgb(60, 170, 255));
    private static readonly IBrush Unsung = Brushes.White;
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(170, 220, 220, 220));

    private bool _attached;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void RequestFrame() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
    {
        if (!_attached) return;
        InvalidateVisual();
        RequestFrame();
    });

    public override void Render(DrawingContext ctx)
    {
        if (DataContext is not SingViewModel vm || vm.Tick() is not { } s) return;
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 100 || h < 100) return;

        var lane = new Rect(w * 0.05, h * 0.10, w * 0.90, h * 0.50);
        ctx.DrawRectangle(LaneBrush, null, lane, 12, 12);
        if (s.Lane is { } layout) DrawLane(ctx, lane, layout, s);

        DrawLyrics(ctx, w, h, s.Lyrics);
        DrawMicLevel(ctx, w, h, s);
        DrawText(ctx, $"{s.Score:N0}", 34, Brushes.White, new Point(w * 0.95, h * 0.025), alignRight: true);

        if (s.LastLine is { } line && s.LastLineAgeBeats is >= 0 and < 12)
            DrawText(ctx, RatingText(line.Rating), 28, GoldenHitBrush, new Point(lane.Right - 16, lane.Top + 12), alignRight: true);

    }

    private static void DrawLane(DrawingContext ctx, Rect lane, NoteLaneLayout layout, StageSnapshot s)
    {
        double barH = Math.Max(8, lane.Height / 16);
        double X(double laneX) => lane.Left + 24 + laneX * (lane.Width - 48);
        double Y(double laneY) => lane.Bottom - 16 - laneY * (lane.Height - 32);
        double beatWidth = (lane.Width - 48) * layout.XFor(1) - (lane.Width - 48) * layout.XFor(0);

        foreach (var n in layout.Layout())
        {
            var bar = new Rect(X(n.X), Y(n.Y) - barH / 2, Math.Max(3, n.Width * (lane.Width - 48)), barH);
            if (n.Note.Type == NoteType.Freestyle) ctx.DrawRectangle(null, FreestylePen, bar, barH / 2, barH / 2);
            else ctx.DrawRectangle(n.Note.IsGolden ? GoldenBrush : NoteBrush, null, bar, barH / 2, barH / 2);
        }

        foreach (var (note, beat) in s.HitBeats)
        {
            if (note.StartBeat < layout.Notes.FirstOrDefault()?.StartBeat) continue;
            var hit = new Rect(X(layout.XFor(beat)), Y(layout.YFor(note.MidiTone)) - barH / 2, beatWidth + 0.5, barH);
            ctx.DrawRectangle(note.IsGolden ? GoldenHitBrush : HitBrush, null, hit);
        }

        double cursorX = X(Math.Clamp(layout.XFor(s.Beat), 0, 1));
        ctx.DrawLine(CursorPen, new Point(cursorX, lane.Top + 8), new Point(cursorX, lane.Bottom - 8));

        // The singer's current pitch, shown while the reading is fresh (within a beat).
        if (s.Pitch is { Pitch.IsVoiced: true } p && Math.Abs(p.Beat - s.Beat) < 2)
            ctx.DrawEllipse(PitchBrush, null, new Point(cursorX, Y(layout.SingerY(p.Pitch.Midi))), barH * 0.6, barH * 0.6);
    }

    private static void DrawLyrics(DrawingContext ctx, double w, double h, LyricsFrame lyrics)
    {
        if (lyrics.Current is { } line)
        {
            double size = Math.Clamp(h * 0.055, 18, 54);
            var parts = line.Syllables.Select(sy => (sy, Text(sy.Text, size, Unsung))).ToArray();
            double total = parts.Sum(p => p.Item2.WidthIncludingTrailingWhitespace);
            double x = (w - total) / 2, y = h * 0.68;

            foreach (var (syllable, text) in parts)
            {
                ctx.DrawText(text, new Point(x, y));
                if (syllable.Progress > 0)
                {
                    // Re-draw the sung part in colour, clipped to the wipe position.
                    using (ctx.PushClip(new Rect(x, y, text.WidthIncludingTrailingWhitespace * syllable.Progress, text.Height)))
                        ctx.DrawText(Text(syllable.Text, size, Sung), new Point(x, y));
                }
                x += text.WidthIncludingTrailingWhitespace;
            }

            // Lead-in: up to three dots counting down the last beats before the line starts.
            if (lyrics.BeatsUntilStart is > 0 and < 16)
            {
                int dots = Math.Clamp((int)Math.Ceiling(lyrics.BeatsUntilStart / 16.0 * 3), 1, 3);
                double startX = (w - total) / 2 - 24 - dots * 18;
                for (int i = 0; i < dots; i++)
                    ctx.DrawEllipse(Sung, null, new Point(startX + i * 18, y + size * 0.65), 6, 6);
            }
        }

        if (lyrics.Next is { } next)
            DrawText(ctx, next.Text, Math.Clamp(h * 0.038, 14, 36), Dim, new Point(w / 2, h * 0.79), center: true);
    }

    /// <summary>A small input meter, bottom right: the quickest way to see whether the microphone hears anything.</summary>
    private static void DrawMicLevel(DrawingContext ctx, double w, double h, StageSnapshot s)
    {
        if (s.Pitch is not { } reading) return;
        const double floorDb = -70;
        double level = Math.Clamp((reading.Pitch.LevelDb - floorDb) / -floorDb, 0, 1);
        double gate = (Singularity.Karaoke.SingerSession.SilenceDb - floorDb) / -floorDb;
        var track = new Rect(w * 0.95 - 160, h * 0.955, 160, 8);
        ctx.DrawRectangle(LaneBrush, null, track, 4, 4);
        ctx.DrawRectangle(reading.Pitch.IsVoiced ? HitBrush : Dim, null, track.WithWidth(Math.Max(2, track.Width * level)), 4, 4);
        ctx.DrawLine(CursorPen, new Point(track.X + track.Width * gate, track.Y - 3), new Point(track.X + track.Width * gate, track.Bottom + 3));
        DrawText(ctx, "mic", 12, Dim, new Point(track.X - 8, track.Y - 4), alignRight: true);
    }

    private static string RatingText(LineRating rating) => rating switch
    {
        LineRating.Perfect => "Perfect!",
        LineRating.Awesome => "Awesome!",
        LineRating.Great => "Great!",
        LineRating.Good => "Good",
        LineRating.NotBad => "Not bad",
        LineRating.Bad => "Bad",
        LineRating.Poor => "Poor",
        _ => "Awful",
    };

    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, size, brush);

    private static void DrawText(DrawingContext ctx, string text, double size, IBrush brush, Point at, bool center = false, bool alignRight = false)
    {
        var formatted = Text(text, size, brush);
        double x = center ? at.X - formatted.Width / 2 : alignRight ? at.X - formatted.Width : at.X;
        ctx.DrawText(formatted, new Point(x, at.Y));
    }
}
