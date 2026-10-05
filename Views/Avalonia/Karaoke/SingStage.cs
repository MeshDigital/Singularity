using System;
using System.Collections.Generic;
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
///
/// Pitch uses one scale for the whole song (see <see cref="NoteLaneLayout"/>): between lines the view glides
/// to the new line's centre instead of jumping. The singer's pitch is folded to the octave of the note being
/// sung and steadied by a <see cref="PitchSmoother"/> before it is drawn.
///
/// The look: a glass lane with faint pitch guides; notes with a gradient and a lit top edge, golden
/// notes glowing; sung parts filled in the singer's colour with a glow; the singer's pitch as a glowing
/// dot trailing the last beats; lyrics outlined so they read over any video; line ratings that pop up
/// and fade; a score that counts up; and a thin song progress bar.
/// </summary>
public sealed class SingStage : Control
{
    private static readonly Typeface Font = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface BoldFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    private static readonly IBrush LaneBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(150, 24, 28, 44), 0), new GradientStop(Color.FromArgb(175, 8, 10, 18), 1) },
    };
    private static readonly IPen LaneBorder = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
    private static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)), 1);
    private static readonly IBrush NoteBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(120, 255, 255, 255), 0), new GradientStop(Color.FromArgb(60, 200, 210, 230), 1) },
    };
    private static readonly IBrush GoldenBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(200, 255, 222, 110), 0), new GradientStop(Color.FromArgb(150, 230, 160, 20), 1) },
    };
    private static readonly IPen NoteEdge = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1);
    private static readonly IPen FreestylePen = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1.5, DashStyle.Dash);
    private static readonly IBrush Unsung = Brushes.White;
    private static readonly IBrush Outline = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IBrush VideoShade = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(185, 225, 228, 235));
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));

    /// <summary>Per-player colours: player 1 blue, player 2 red.</summary>
    private static readonly Color[] PlayerColours = { Color.FromRgb(60, 170, 255), Color.FromRgb(255, 90, 100) };

    private static Color ColourOf(int player) => PlayerColours[(player - 1) % PlayerColours.Length];

    private bool _attached;

    /// <summary>Per player: the recent pitch readings (beat, MIDI) for the trail, and the score as shown (counting up).</summary>
    private readonly Dictionary<int, List<(double Beat, double Midi)>> _trails = new();
    private readonly Dictionary<int, double> _shownScores = new();
    private readonly Dictionary<int, PitchSmoother> _smoothers = new();
    private readonly Dictionary<int, Glide> _glides = new();

    /// <summary>How long the view takes to move to a new line's pitch centre.</summary>
    private const double GlideMs = 400;

    /// <summary>The lane's pitch centre moving smoothly from one line's centre to the next (ease in and out).</summary>
    private sealed class Glide
    {
        private double _from, _to;
        private long _start;

        public Glide(double centre) => _from = _to = centre;

        public double At(double target, long nowMs)
        {
            if (target != _to)
            {
                _from = At(_to, nowMs);
                _to = target;
                _start = nowMs;
            }
            double t = Math.Clamp((nowMs - _start) / GlideMs, 0, 1);
            double eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            return _from + (_to - _from) * eased;
        }
    }

    /// <summary>The pitch centre to draw a player's lane at this frame.</summary>
    private double CentreFor(int player, NoteLaneLayout layout)
    {
        long now = Environment.TickCount64;
        if (!_glides.TryGetValue(player, out var glide)) _glides[player] = glide = new Glide(layout.Centre);
        return glide.At(layout.Centre, now);
    }

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
        if (_frame is { } f) Remember(f);
        InvalidateVisual();
        RequestFrame();
    });

    /// <summary>Keeps the pitch trail and the counting score between frames.</summary>
    private void Remember(StageSnapshot s)
    {
        foreach (var p in s.Players)
        {
            if (!_trails.TryGetValue(p.Player, out var trail)) _trails[p.Player] = trail = new();
            if (trail.Count > 0 && s.Beat < trail[^1].Beat - 1) trail.Clear(); // restarted or seeked back
            if (!_smoothers.TryGetValue(p.Player, out var smoother)) _smoothers[p.Player] = smoother = new PitchSmoother();
            if (p.Pitch is { } r && Math.Abs(r.Beat - s.Beat) < 2 && (trail.Count == 0 || r.Beat > trail[^1].Beat))
            {
                // Folded to the note being sung, then steadied: what the dot and trail show.
                double? folded = r.Pitch.IsVoiced && p.Lane is { } lane ? lane.FoldToTarget(r.Pitch.Midi, r.Beat) : null;
                if (smoother.Add(folded) is { } smooth) trail.Add((r.Beat, smooth));
            }
            else if (p.Pitch is null) smoother.Reset();
            trail.RemoveAll(t => t.Beat < s.Beat - TrailBeats);

            double shown = _shownScores.GetValueOrDefault(p.Player);
            shown = p.Score < shown ? p.Score : shown + (p.Score - shown) * 0.12; // ease towards the score; drop at once on restart
            if (Math.Abs(p.Score - shown) < 1) shown = p.Score;
            _shownScores[p.Player] = shown;
        }
    }

    /// <summary>How many beats of the singer's pitch stay visible behind the cursor.</summary>
    private const double TrailBeats = 6;

    public override void Render(DrawingContext ctx)
    {
        if (DataContext is not SingViewModel || _frame is not { } s) return;
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 100 || h < 100) return;

        if (s.Video is { } video) DrawVideo(ctx, video, w, h, s.VideoAmbient);

        // Everything scales with the stage height (so a 1080p or 4K projector gets proportionally large,
        // sharp text) and with the user's text size; minimums keep it readable in a small window.
        double k = s.TextScale;
        if (s.Jukebox)
        {
            // Listening, not singing: just the lyrics, a little larger, and a label.
            if (s.Players.Count > 0)
                DrawLyrics(ctx, w, s.Players[0].Lyrics, h * 0.66, h * 0.66 + h * 0.085 * k + 12, Math.Max(18, h * 0.065 * k), Color.FromRgb(120, 220, 200));
            DrawOutlinedText(ctx, "Jukebox", Math.Max(14, h * 0.022), Dim, new Point(w - 24, 18), alignRight: true, bold: true);
            DrawSongProgress(ctx, s.SongProgress, w, h);
            return;
        }
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

        DrawSongProgress(ctx, s.SongProgress, w, h);
    }

    private void DrawPlayer(DrawingContext ctx, StageSnapshot s, PlayerSnapshot p, Rect lane, double lyricsY, double? nextY,
        double lyricSize, double w, double h, double k, bool showName)
    {
        var colour = ColourOf(p.Player);
        var colourBrush = new SolidColorBrush(colour);
        ctx.DrawRectangle(LaneBrush, LaneBorder, new RoundedRect(lane, 14), new BoxShadows(new BoxShadow { Blur = 24, Color = Color.FromArgb(90, 0, 0, 0), OffsetY = 6 }));
        if (p.Lane is { } layout) DrawLane(ctx, lane, layout, CentreFor(p.Player, layout), s.Beat, p, colour, k, _trails.GetValueOrDefault(p.Player));

        double scoreSize = Math.Max(18, h * 0.034 * k), ratingSize = Math.Max(18, h * 0.03 * k);
        int shownScore = (int)Math.Round(_shownScores.GetValueOrDefault(p.Player, p.Score));
        DrawOutlinedText(ctx, $"{shownScore:N0}", scoreSize, showName ? colourBrush : Brushes.White, new Point(lane.Right - 18, lane.Top + 10), alignRight: true, bold: true);
        if (showName) DrawOutlinedText(ctx, $"P{p.Player}", Math.Max(14, h * 0.02 * k), colourBrush, new Point(lane.Left + 16, lane.Top + 10), bold: true);

        if (p.LastLine is { } line && p.LastLineAgeBeats is >= 0 and < RatingBeats)
            DrawRating(ctx, line.Rating, p.LastLineAgeBeats, ratingSize, new Point(lane.Right - 18, lane.Top + 16 + scoreSize * 1.25));

        DrawLyrics(ctx, w, p.Lyrics, lyricsY, nextY, lyricSize, colour);
        DrawMicLevel(ctx, p, new Point(lane.Right - 160, lane.Bottom + 8));
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

    private static void DrawLane(DrawingContext ctx, Rect lane, NoteLaneLayout layout, double centre, double beat, PlayerSnapshot p, Color colour, double k,
        List<(double Beat, double Midi)>? trail)
    {
        // One semitone is the same height on every line; a bar is about a semitone tall.
        double semitone = (lane.Height - 32) / layout.Span;
        double barH = Math.Max(6, Math.Min(semitone * 1.15 * Math.Sqrt(k), lane.Height / 8));
        double X(double laneX) => lane.Left + 24 + laneX * (lane.Width - 48);
        double Y(double laneY) => lane.Bottom - 16 - laneY * (lane.Height - 32);
        double PitchY(double midi) => Y(layout.YFor(midi, centre));
        double beatWidth = (lane.Width - 48) * (layout.XFor(1) - layout.XFor(0));

        // Faint guides on every other semitone, moving with the view, so intervals read at a glance.
        for (int m = (int)Math.Ceiling(centre - layout.Span / 2); m <= centre + layout.Span / 2; m++)
        {
            if (m % 2 != 0) continue;
            double gy = PitchY(m);
            if (gy > lane.Top + 8 && gy < lane.Bottom - 8)
                ctx.DrawLine(GuidePen, new Point(lane.Left + 16, gy), new Point(lane.Right - 16, gy));
        }

        foreach (var n in layout.Layout(centre))
        {
            var bar = new Rect(X(n.X), Y(n.Y) - barH / 2, Math.Max(3, n.Width * (lane.Width - 48)), barH);
            if (n.Note.Type == NoteType.Freestyle)
            {
                ctx.DrawRectangle(null, FreestylePen, bar, barH / 2, barH / 2);
                continue;
            }
            var shape = new RoundedRect(bar, barH / 2);
            if (n.Note.IsGolden)
                ctx.DrawRectangle(GoldenBrush, NoteEdge, shape, new BoxShadows(new BoxShadow { Blur = barH * 1.4, Color = Color.FromArgb(130, 255, 190, 40) }));
            else
                ctx.DrawRectangle(NoteBrush, NoteEdge, shape);
        }

        // What was sung, filled in and glowing; adjacent beats of one note join into one bar. In tune it is the
        // singer's colour (gold on golden notes); sharp it turns amber and sits a little higher, flat it turns
        // cyan and sits a little lower, by how far off it was.
        int firstStart = layout.Notes.FirstOrDefault()?.StartBeat ?? int.MinValue;
        foreach (var run in Runs(p.HitBeats.Where(h => h.Note.StartBeat >= firstStart)))
        {
            var tint = run.Tune switch
            {
                Intonation.Sharp => SharpColour,
                Intonation.Flat => FlatColour,
                _ => run.Note.IsGolden ? Color.FromRgb(255, 210, 60) : colour,
            };
            double lift = Math.Clamp(run.Offset, -0.5, 0.5) * semitone;
            var rect = new Rect(X(layout.XFor(run.From)), PitchY(run.Note.MidiTone) - lift - barH / 2, beatWidth * (run.To - run.From + 1) + 0.5, barH);
            var glow = new BoxShadows(new BoxShadow { Blur = barH * (run.Note.IsGolden ? 1.6 : 1.2), Color = Color.FromArgb(160, tint.R, tint.G, tint.B) });
            ctx.DrawRectangle(new SolidColorBrush(tint), null, new RoundedRect(rect, barH / 2), glow);
        }

        double cursorX = X(Math.Clamp(layout.XFor(beat), 0, 1));
        var cursorBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0, 255, 255, 255), 0), new GradientStop(Color.FromArgb(150, 255, 255, 255), 0.5), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
        };
        ctx.DrawRectangle(cursorBrush, null, new Rect(cursorX - 1, lane.Top + 6, 2, lane.Height - 12));

        // The singer's pitch over the last beats: a fading trail into a glowing dot.
        if (trail is { Count: > 0 })
        {
            double dot = barH * 0.55;
            for (int i = 0; i < trail.Count; i++)
            {
                var (b, midi) = trail[i];
                double age = (beat - b) / TrailBeats;
                if (age < 0 || age > 1) continue;
                var at = new Point(X(Math.Clamp(layout.XFor(b), 0, 1)), PitchY(midi));
                ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(140 * (1 - age)), colour.R, colour.G, colour.B)), null, at, dot * (0.35 + 0.4 * (1 - age)), dot * (0.35 + 0.4 * (1 - age)));
            }
        }
        // The dot: the newest steadied reading, drawn halfway onto the note when it is nearly on it.
        if (p.Pitch is { Pitch.IsVoiced: true } r && Math.Abs(r.Beat - beat) < 2 && trail is { Count: > 0 } && beat - trail[^1].Beat < 0.5)
        {
            double midi = trail[^1].Midi;
            if (layout.TargetAt(beat) is { } target) midi = PitchSmoother.Snap(midi, target.MidiTone);
            var at = new Point(cursorX, PitchY(midi));
            double dot = barH * 0.6;
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, colour.R, colour.G, colour.B)), null, at, dot * 2.1, dot * 2.1);
            ctx.DrawEllipse(new SolidColorBrush(colour), new Pen(Brushes.White, 1.5), at, dot, dot);
        }
    }

    private enum Intonation { InTune, Sharp, Flat }

    /// <summary>Further off than this (18 cents) a sung beat shows as sharp or flat.</summary>
    private const double InTuneSemitones = 0.18;

    private static readonly Color SharpColour = Color.FromRgb(255, 170, 60);
    private static readonly Color FlatColour = Color.FromRgb(70, 225, 235);

    private static Intonation TuneOf(double offset) =>
        offset > InTuneSemitones ? Intonation.Sharp : offset < -InTuneSemitones ? Intonation.Flat : Intonation.InTune;

    /// <summary>Hit beats grouped into runs of consecutive beats of the same note sung in the same way (in tune, sharp, flat).</summary>
    private static IEnumerable<(UltraStarNote Note, int From, int To, Intonation Tune, double Offset)> Runs(IEnumerable<BeatJudgement> hits)
    {
        UltraStarNote? note = null;
        int from = 0, to = 0;
        var tune = Intonation.InTune;
        double offsetSum = 0;
        foreach (var h in hits.OrderBy(h => h.Note.StartBeat).ThenBy(h => h.Beat))
        {
            var t = TuneOf(h.Offset);
            if (note is not null && ReferenceEquals(h.Note, note) && h.Beat == to + 1 && t == tune)
            {
                to = h.Beat;
                offsetSum += h.Offset;
                continue;
            }
            if (note is not null) yield return (note, from, to, tune, offsetSum / (to - from + 1));
            (note, from, to, tune, offsetSum) = (h.Note, h.Beat, h.Beat, t, h.Offset);
        }
        if (note is not null) yield return (note, from, to, tune, offsetSum / (to - from + 1));
    }

    private static void DrawLyrics(DrawingContext ctx, double w, LyricsFrame lyrics, double y, double? nextY, double size, Color colour)
    {
        var sung = new SolidColorBrush(colour);
        if (lyrics.Current is { } line)
        {
            var parts = line.Syllables.Select(sy => (sy, Text(sy.Text, size, Unsung))).ToArray();
            double total = parts.Sum(p => p.Item2.WidthIncludingTrailingWhitespace);
            double x = (w - total) / 2;

            foreach (var (syllable, text) in parts)
            {
                DrawOutline(ctx, syllable.Text, size, new Point(x, y), Font);
                ctx.DrawText(text, new Point(x, y));
                if (syllable.Progress > 0)
                {
                    // Re-draw the sung part in the player's colour, clipped to the wipe position.
                    using (ctx.PushClip(new Rect(x, y - size * 0.2, text.WidthIncludingTrailingWhitespace * syllable.Progress, text.Height + size * 0.4)))
                        ctx.DrawText(Text(syllable.Text, size, sung), new Point(x, y));
                }
                x += text.WidthIncludingTrailingWhitespace;
            }

            // Lead-in: up to three dots counting down the last beats before the line starts, the next one pulsing.
            if (lyrics.BeatsUntilStart is > 0 and < 16)
            {
                int dots = Math.Clamp((int)Math.Ceiling(lyrics.BeatsUntilStart / 16.0 * 3), 1, 3);
                double dot = Math.Max(5, size * 0.12), gap = dot * 3;
                double startX = (w - total) / 2 - gap - dots * gap;
                double pulse = 1 + 0.25 * Math.Abs(Math.Sin(lyrics.BeatsUntilStart * Math.PI / 4));
                for (int i = 0; i < dots; i++)
                {
                    double r = i == dots - 1 ? dot * pulse : dot;
                    ctx.DrawEllipse(sung, null, new Point(startX + i * gap, y + size * 0.65), r, r);
                }
            }
        }

        if (nextY is { } ny && lyrics.Next is { } next)
            DrawOutlinedText(ctx, next.Text, size * 0.7, Dim, new Point(w / 2, ny), center: true);
    }

    /// <summary>How many beats a line rating stays up.</summary>
    private const double RatingBeats = 14;

    /// <summary>The line's rating: pops in slightly large, settles, rises a little and fades out.</summary>
    private static void DrawRating(DrawingContext ctx, LineRating rating, double ageBeats, double size, Point at)
    {
        double t = ageBeats / RatingBeats;
        double scale = t < 0.12 ? 1.25 - t / 0.12 * 0.25 : 1;
        double opacity = t < 0.7 ? 1 : 1 - (t - 0.7) / 0.3;
        var brush = new SolidColorBrush(RatingColour(rating));
        using (ctx.PushOpacity(Math.Clamp(opacity, 0, 1)))
            DrawOutlinedText(ctx, RatingText(rating), size * scale, brush, new Point(at.X, at.Y - t * size * 0.6), alignRight: true, bold: true);
    }

    private static Color RatingColour(LineRating rating) => rating switch
    {
        LineRating.Perfect => Color.FromRgb(255, 214, 70),
        LineRating.Awesome => Color.FromRgb(120, 230, 160),
        LineRating.Great => Color.FromRgb(110, 210, 255),
        LineRating.Good => Color.FromRgb(210, 220, 235),
        _ => Color.FromRgb(170, 175, 185),
    };

    /// <summary>A small input meter under the lane: the quickest way to see whether a microphone hears anything.</summary>
    private static void DrawMicLevel(DrawingContext ctx, PlayerSnapshot p, Point at)
    {
        if (p.Pitch is not { } reading) return;
        const double floorDb = -70;
        double level = Math.Clamp((reading.Pitch.LevelDb - floorDb) / -floorDb, 0, 1);
        double gate = (Singularity.Karaoke.SingerSession.SilenceDb - floorDb) / -floorDb;
        var track = new Rect(at.X, at.Y, 160, 6);
        ctx.DrawRectangle(TrackBrush, null, track, 3, 3);
        ctx.DrawRectangle(reading.Pitch.IsVoiced ? new SolidColorBrush(ColourOf(p.Player)) : Dim, null, track.WithWidth(Math.Max(2, track.Width * level)), 3, 3);
        ctx.DrawRectangle(Brushes.White, null, new Rect(track.X + track.Width * gate - 1, track.Y - 3, 2, track.Height + 6));
    }

    /// <summary>A thin line along the bottom: how far through the song.</summary>
    private static void DrawSongProgress(DrawingContext ctx, double progress, double w, double h)
    {
        if (progress <= 0) return;
        var track = new Rect(0, h - 4, w, 4);
        ctx.DrawRectangle(TrackBrush, null, track);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), null, track.WithWidth(w * progress));
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

    private static FormattedText Text(string text, double size, IBrush brush, Typeface? font = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, font ?? Font, size, brush);

    /// <summary>A dark outline (the text drawn slightly offset around it) so text reads over any video.</summary>
    private static void DrawOutline(DrawingContext ctx, string text, double size, Point at, Typeface font)
    {
        var shadow = Text(text, size, Outline, font);
        double o = Math.Max(1, size * 0.045);
        ctx.DrawText(shadow, new Point(at.X + o, at.Y + o * 1.6)); // drop shadow
        ctx.DrawText(shadow, new Point(at.X - o, at.Y));
        ctx.DrawText(shadow, new Point(at.X + o, at.Y));
        ctx.DrawText(shadow, new Point(at.X, at.Y - o));
        ctx.DrawText(shadow, new Point(at.X, at.Y + o));
    }

    private static void DrawOutlinedText(DrawingContext ctx, string text, double size, IBrush brush, Point at, bool center = false, bool alignRight = false, bool bold = false)
    {
        var font = bold ? BoldFont : Font;
        var formatted = Text(text, size, brush, font);
        double x = center ? at.X - formatted.Width / 2 : alignRight ? at.X - formatted.Width : at.X;
        DrawOutline(ctx, text, size, new Point(x, at.Y), font);
        ctx.DrawText(formatted, new Point(x, at.Y));
    }
}
