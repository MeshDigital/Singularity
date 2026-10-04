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
/// Draws the sing screen. With one singer: a note lane in the upper half and the lyrics below it.
/// With two: player 1's lane and lyrics in the top third, player 2's in the bottom third, and the
/// video visible between them. Each player gets their own colour, score, pitch marker and line
/// ratings. The stage redraws once per display refresh (TopLevel.RequestAnimationFrame) from the
/// view model's snapshot. Every position derives from the audio device clock in that snapshot, so a
/// late frame shows the right moment instead of drifting.
/// </summary>
public sealed class SingStage : Control
{
    private static readonly Typeface Font = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IBrush LaneBrush = new SolidColorBrush(Color.FromArgb(150, 10, 12, 20));
    private static readonly IBrush NoteBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
    private static readonly IBrush GoldenBrush = new SolidColorBrush(Color.FromArgb(150, 255, 200, 40));
    private static readonly IBrush GoldenHitBrush = new SolidColorBrush(Color.FromRgb(255, 210, 60));
    private static readonly IPen FreestylePen = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1.5, DashStyle.Dash);
    private static readonly IPen CursorPen = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 2);
    private static readonly IBrush Unsung = Brushes.White;
    private static readonly IBrush VideoShade = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(170, 220, 220, 220));

    /// <summary>Per-player colours: player 1 blue, player 2 red.</summary>
    private static readonly IBrush[] PlayerBrushes =
    {
        new SolidColorBrush(Color.FromRgb(60, 170, 255)),
        new SolidColorBrush(Color.FromRgb(255, 90, 100)),
    };

    private static IBrush ColourOf(int player) => PlayerBrushes[(player - 1) % PlayerBrushes.Length];

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

    /// <summary>The frame to draw, taken before the render pass.</summary>
    private StageSnapshot? _frame;

    // The view model is advanced here, before the render pass, never inside Render: Tick updates bound
    // properties (live scores, results), and changing a bound control while Avalonia renders throws
    // "Visual was invalidated during the render pass".
    private void RequestFrame() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
    {
        if (!_attached) return;
        _frame = (DataContext as SingViewModel)?.Tick();
        InvalidateVisual();
        RequestFrame();
    });

    public override void Render(DrawingContext ctx)
    {
        if (DataContext is not SingViewModel || _frame is not { } s) return;
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 100 || h < 100) return;

        if (s.Video is { } video) DrawVideo(ctx, video, w, h, s.VideoAmbient);

        // Everything scales with the stage height (so a 1080p or 4K projector gets proportionally large,
        // sharp text) and with the user's text size; minimums keep it readable in a small window.
        double k = s.TextScale;
        if (s.Players.Count == 1)
        {
            DrawPlayer(ctx, s, s.Players[0], new Rect(w * 0.05, h * 0.10, w * 0.90, h * 0.50), lyricsY: h * 0.68, nextY: h * 0.68 + h * 0.075 * k + 12,
                lyricSize: Math.Max(16, h * 0.055 * k), w, h, k, showName: false);
        }
        else
        {
            // Two singers: lanes at the top and bottom, video visible between them.
            double lyricSize = Math.Max(14, h * 0.04 * k);
            DrawPlayer(ctx, s, s.Players[0], new Rect(w * 0.05, h * 0.07, w * 0.90, h * 0.24), lyricsY: h * 0.315, nextY: null, lyricSize, w, h, k, showName: true);
            DrawPlayer(ctx, s, s.Players[1], new Rect(w * 0.05, h * 0.60, w * 0.90, h * 0.24), lyricsY: h * 0.845, nextY: null, lyricSize, w, h, k, showName: true);
        }
    }

    private static void DrawPlayer(DrawingContext ctx, StageSnapshot s, PlayerSnapshot p, Rect lane, double lyricsY, double? nextY,
        double lyricSize, double w, double h, double k, bool showName)
    {
        var colour = ColourOf(p.Player);
        ctx.DrawRectangle(LaneBrush, null, lane, 12, 12);
        if (p.Lane is { } layout) DrawLane(ctx, lane, layout, s.Beat, p, colour, k);

        double scoreSize = Math.Max(18, h * 0.032 * k), ratingSize = Math.Max(16, h * 0.026 * k);
        DrawText(ctx, $"{p.Score:N0}", scoreSize, showName ? colour : Brushes.White, new Point(lane.Right - 16, lane.Top + 8), alignRight: true);
        if (showName) DrawText(ctx, $"P{p.Player}", Math.Max(14, h * 0.018 * k), colour, new Point(lane.Left + 14, lane.Top + 8));

        if (p.LastLine is { } line && p.LastLineAgeBeats is >= 0 and < 12)
            DrawText(ctx, RatingText(line.Rating), ratingSize, GoldenHitBrush, new Point(lane.Right - 16, lane.Top + 12 + scoreSize * 1.3), alignRight: true);

        DrawLyrics(ctx, w, p.Lyrics, lyricsY, nextY, lyricSize, colour);
        DrawMicLevel(ctx, p, new Point(lane.Right - 160, lane.Bottom + 6));
    }

    /// <summary>
    /// The music video as the bottom layer, filling the stage, dimmed so notes and lyrics stay readable.
    /// A video that isn't synced to the song is only scenery: much dimmer, with a dark vignette, so
    /// lips that don't match the words don't catch the eye.
    /// </summary>
    private static void DrawVideo(DrawingContext ctx, IImage video, double w, double h, bool ambient)
    {
        var size = video.Size;
        double scale = Math.Max(w / size.Width, h / size.Height);
        var dest = new Rect((w - size.Width * scale) / 2, (h - size.Height * scale) / 2, size.Width * scale, size.Height * scale);
        if (ambient)
        {
            using (ctx.PushOpacity(AmbientVideoOpacity)) ctx.DrawImage(video, new Rect(size), dest);
            ctx.DrawRectangle(Vignette, null, new Rect(0, 0, w, h));
            return;
        }
        ctx.DrawImage(video, new Rect(size), dest);
        ctx.DrawRectangle(VideoShade, null, new Rect(0, 0, w, h));
    }

    private const double AmbientVideoOpacity = 0.38;

    private static readonly IBrush Vignette = new RadialGradientBrush
    {
        GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.45), new GradientStop(Color.FromArgb(215, 0, 0, 0), 1.0) },
    };

    private static void DrawLane(DrawingContext ctx, Rect lane, NoteLaneLayout layout, double beat, PlayerSnapshot p, IBrush colour, double k)
    {
        double barH = Math.Max(6, lane.Height / 16 * Math.Sqrt(k));
        double X(double laneX) => lane.Left + 24 + laneX * (lane.Width - 48);
        double Y(double laneY) => lane.Bottom - 16 - laneY * (lane.Height - 32);
        double beatWidth = (lane.Width - 48) * (layout.XFor(1) - layout.XFor(0));

        foreach (var n in layout.Layout())
        {
            var bar = new Rect(X(n.X), Y(n.Y) - barH / 2, Math.Max(3, n.Width * (lane.Width - 48)), barH);
            if (n.Note.Type == NoteType.Freestyle) ctx.DrawRectangle(null, FreestylePen, bar, barH / 2, barH / 2);
            else ctx.DrawRectangle(n.Note.IsGolden ? GoldenBrush : NoteBrush, null, bar, barH / 2, barH / 2);
        }

        int firstStart = layout.Notes.FirstOrDefault()?.StartBeat ?? int.MinValue;
        foreach (var (note, hitBeat) in p.HitBeats)
        {
            if (note.StartBeat < firstStart) continue;
            var hit = new Rect(X(layout.XFor(hitBeat)), Y(layout.YFor(note.MidiTone)) - barH / 2, beatWidth + 0.5, barH);
            ctx.DrawRectangle(note.IsGolden ? GoldenHitBrush : colour, null, hit);
        }

        double cursorX = X(Math.Clamp(layout.XFor(beat), 0, 1));
        ctx.DrawLine(CursorPen, new Point(cursorX, lane.Top + 8), new Point(cursorX, lane.Bottom - 8));

        // The singer's current pitch, shown while the reading is fresh (within two beats).
        if (p.Pitch is { Pitch.IsVoiced: true } r && Math.Abs(r.Beat - beat) < 2)
            ctx.DrawEllipse(colour, null, new Point(cursorX, Y(layout.SingerY(r.Pitch.Midi))), barH * 0.6, barH * 0.6);
    }

    private static void DrawLyrics(DrawingContext ctx, double w, LyricsFrame lyrics, double y, double? nextY, double size, IBrush colour)
    {
        if (lyrics.Current is { } line)
        {
            var parts = line.Syllables.Select(sy => (sy, Text(sy.Text, size, Unsung))).ToArray();
            double total = parts.Sum(p => p.Item2.WidthIncludingTrailingWhitespace);
            double x = (w - total) / 2;

            foreach (var (syllable, text) in parts)
            {
                ctx.DrawText(text, new Point(x, y));
                if (syllable.Progress > 0)
                {
                    // Re-draw the sung part in the player's colour, clipped to the wipe position.
                    using (ctx.PushClip(new Rect(x, y, text.WidthIncludingTrailingWhitespace * syllable.Progress, text.Height)))
                        ctx.DrawText(Text(syllable.Text, size, colour), new Point(x, y));
                }
                x += text.WidthIncludingTrailingWhitespace;
            }

            // Lead-in: up to three dots counting down the last beats before the line starts.
            if (lyrics.BeatsUntilStart is > 0 and < 16)
            {
                int dots = Math.Clamp((int)Math.Ceiling(lyrics.BeatsUntilStart / 16.0 * 3), 1, 3);
                double startX = (w - total) / 2 - 24 - dots * 18;
                double dot = Math.Max(5, size * 0.12), gap = dot * 3;
                startX = (w - total) / 2 - gap - dots * gap;
                for (int i = 0; i < dots; i++)
                    ctx.DrawEllipse(colour, null, new Point(startX + i * gap, y + size * 0.65), dot, dot);
            }
        }

        if (nextY is { } ny && lyrics.Next is { } next)
            DrawText(ctx, next.Text, size * 0.7, Dim, new Point(w / 2, ny), center: true);
    }

    /// <summary>A small input meter under the lane: the quickest way to see whether a microphone hears anything.</summary>
    private static void DrawMicLevel(DrawingContext ctx, PlayerSnapshot p, Point at)
    {
        if (p.Pitch is not { } reading) return;
        const double floorDb = -70;
        double level = Math.Clamp((reading.Pitch.LevelDb - floorDb) / -floorDb, 0, 1);
        double gate = (Singularity.Karaoke.SingerSession.SilenceDb - floorDb) / -floorDb;
        var track = new Rect(at.X, at.Y, 160, 6);
        ctx.DrawRectangle(LaneBrush, null, track, 3, 3);
        ctx.DrawRectangle(reading.Pitch.IsVoiced ? ColourOf(p.Player) : Dim, null, track.WithWidth(Math.Max(2, track.Width * level)), 3, 3);
        ctx.DrawLine(CursorPen, new Point(track.X + track.Width * gate, track.Y - 3), new Point(track.X + track.Width * gate, track.Bottom + 3));
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
