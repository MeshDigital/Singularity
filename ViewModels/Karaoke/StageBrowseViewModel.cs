using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using SkiaSharp;

namespace Singularity.ViewModels.Karaoke;

/// <summary>
/// Song select for the projector, built for reading from across the room: the highlighted song big in
/// the middle with its neighbours either side, the room glowing in the cover's colour. Left and Right
/// move between songs, Up and Down between a song's versions, Enter sings. It shares the list and the
/// highlighted song with the laptop's song select, so either screen can drive it.
/// </summary>
public sealed class StageBrowseViewModel : ReactiveObject
{
    public const int Neighbours = 2;
    public const int LargeCoverWidth = 720;

    private static readonly ConcurrentDictionary<string, Color> Glows = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Color DefaultGlow = Color.FromRgb(0x5B, 0x3F, 0xA8);

    private readonly SongSelectViewModel _songs;
    private SongCardViewModel? _current;
    private Bitmap? _largeCover;
    private Color _glow = DefaultGlow;
    private int _load;

    public StageBrowseViewModel(SongSelectViewModel songs)
    {
        _songs = songs;
        _songs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SongSelectViewModel.SelectedSong)) Follow();
            else if (e.PropertyName == nameof(SongSelectViewModel.PreviewVideoPath)) this.RaisePropertyChanged(nameof(PreviewVideoPath));
            else if (e.PropertyName == nameof(SongSelectViewModel.PreviewVideoGapMs)) this.RaisePropertyChanged(nameof(PreviewVideoGapMs));
            else if (e.PropertyName == nameof(SongSelectViewModel.HasPreviewVideo)) this.RaisePropertyChanged(nameof(HasPreviewVideo));
        };
        _songs.Songs.CollectionChanged += (_, _) => Follow();
        _songs.UpNext.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(UpNextText));
            this.RaisePropertyChanged(nameof(HasUpNext));
        };
        Follow();
    }

    /// <summary>The next three singers for the room: "Alice · Mr. Brightside     Bob · Hot N Cold".</summary>
    public string UpNextText => string.Join("      ", _songs.UpNext.Take(3).Select(q => $"{q.Place}. {q.Singer} · {q.Item.Title}"))
        + (_songs.UpNext.Count > 3 ? $"      +{_songs.UpNext.Count - 3} more" : "");

    public bool HasUpNext => _songs.UpNext.Count > 0;

    public SongCardViewModel? Current => _current;

    /// <summary>The highlighted song's music video while its preview plays (shown in place of the big cover).</summary>
    public string? PreviewVideoPath => _songs.PreviewVideoPath;
    public double PreviewVideoGapMs => _songs.PreviewVideoGapMs;
    public bool HasPreviewVideo => _songs.HasPreviewVideo;
    public Func<double?> PreviewClock => _songs.PreviewClock;
    public bool HasSong => _current is not null;
    public IReadOnlyList<SongCardViewModel> Before { get; private set; } = Array.Empty<SongCardViewModel>();
    public IReadOnlyList<SongCardViewModel> After { get; private set; } = Array.Empty<SongCardViewModel>();

    /// <summary>The highlighted song's cover, decoded large; null while loading or without a cover.</summary>
    public Bitmap? LargeCover { get => _largeCover; private set => this.RaiseAndSetIfChanged(ref _largeCover, value); }

    /// <summary>The cover's dominant colour, for the glow behind everything.</summary>
    public Color Glow { get => _glow; private set => this.RaiseAndSetIfChanged(ref _glow, value); }

    /// <summary>"▲  2 of 3 · Duet · Community chart  ▼", or just the label for a song with one version.</summary>
    /// <summary>"Best 8,700": the song's best score at any difficulty, for the room to beat; null when never sung.</summary>
    public string? BestScoreBadge => _current?.BestScore is { } best ? $"Best {best:N0}" : null;

    public bool HasBestScore => BestScoreBadge is not null;

    public string VersionBadge => _current is null ? "" : _current.HasVersions ? $"▲   {_current.VersionText}   ▼" : _current.VersionText;

    /// <summary>How many cards peek out behind the cover: one per extra version, at most two.</summary>
    public int StackDepth => _current is null ? 0 : Math.Min(2, _current.Cluster.Versions.Count - 1);
    public bool HasStack1 => StackDepth >= 1;
    public bool HasStack2 => StackDepth >= 2;

    public string Position => _current is null ? "" : $"{_songs.Songs.IndexOf(_current) + 1} / {_songs.Songs.Count}";

    public void MoveSong(int step)
    {
        var list = _songs.Songs;
        if (list.Count == 0) return;
        int i = _current is null ? 0 : list.IndexOf(_current);
        _songs.SelectedSong = list[Math.Clamp(i + step, 0, list.Count - 1)];
    }

    public void MoveVersion(int step) => _songs.StepVersion(_current, step);

    public void Sing()
    {
        if (_current is not null && _songs.SingCommand.CanExecute(_current)) _songs.SingCommand.Execute(_current);
    }

    private void Follow()
    {
        var list = _songs.Songs;
        var current = _songs.SelectedSong is { } s && list.Contains(s) ? s : list.FirstOrDefault();
        if (!ReferenceEquals(current, _current))
        {
            if (_current is not null) _current.PropertyChanged -= OnCardChanged;
            _current = current;
            if (_current is not null) _current.PropertyChanged += OnCardChanged;
            this.RaisePropertyChanged(nameof(Current));
            this.RaisePropertyChanged(nameof(HasSong));
            LoadArt();
        }

        int index = current is null ? -1 : list.IndexOf(current);
        Before = index < 0 ? Array.Empty<SongCardViewModel>() : list.Skip(Math.Max(0, index - Neighbours)).Take(index - Math.Max(0, index - Neighbours)).ToList();
        After = index < 0 ? Array.Empty<SongCardViewModel>() : list.Skip(index + 1).Take(Neighbours).ToList();
        this.RaisePropertyChanged(nameof(Before));
        this.RaisePropertyChanged(nameof(After));
        RaiseVersion();
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SongCardViewModel.Entry)) return;
        RaiseVersion();
        LoadArt();
    }

    private void RaiseVersion()
    {
        foreach (var name in new[] { nameof(VersionBadge), nameof(BestScoreBadge), nameof(HasBestScore), nameof(StackDepth), nameof(HasStack1), nameof(HasStack2), nameof(Position) })
            this.RaisePropertyChanged(name);
    }

    /// <summary>Decodes the large cover and its glow colour off the UI thread; a newer selection wins.</summary>
    private void LoadArt()
    {
        int load = ++_load;
        var path = _current?.Entry.CoverPath;
        LargeCover = null;
        if (path is null)
        {
            Glow = DefaultGlow;
            return;
        }
        _ = Task.Run(() =>
        {
            Bitmap? bitmap = null;
            try
            {
                using var stream = File.OpenRead(path);
                bitmap = Bitmap.DecodeToWidth(stream, LargeCoverWidth, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                Serilog.Log.Debug(ex, "Cover {Path} could not be decoded", path);
            }
            var glow = Glows.GetOrAdd(path, DominantColor);
            Dispatcher.UIThread.Post(() =>
            {
                if (load != _load)
                {
                    bitmap?.Dispose();
                    return;
                }
                var old = LargeCover;
                LargeCover = bitmap;
                old?.Dispose();
                Glow = glow;
            });
        });
    }

    /// <summary>
    /// The colour the cover is mostly made of, favouring vivid pixels (a black sleeve with a red logo
    /// glows red), kept bright enough to light up a dark room.
    /// </summary>
    internal static Color DominantColor(string path)
    {
        try
        {
            using var full = SKBitmap.Decode(path);
            if (full is null) return DefaultGlow;
            using var small = full.Resize(new SKImageInfo(24, 24), SKFilterQuality.Medium);
            if (small is null) return DefaultGlow;
            double r = 0, g = 0, b = 0, weight = 0;
            for (int y = 0; y < small.Height; y++)
            for (int x = 0; x < small.Width; x++)
            {
                var c = small.GetPixel(x, y);
                c.ToHsv(out _, out var sat, out var val);
                double w = 0.05 + (sat / 100.0) * (val / 100.0);
                r += c.Red * w; g += c.Green * w; b += c.Blue * w; weight += w;
            }
            var mixed = new SKColor((byte)(r / weight), (byte)(g / weight), (byte)(b / weight));
            mixed.ToHsv(out var h, out var s, out var v);
            var lit = SKColor.FromHsv(h, Math.Max(s, 45), Math.Max(v, 55));
            return Color.FromRgb(lit.Red, lit.Green, lit.Blue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DefaultGlow;
        }
    }
}
