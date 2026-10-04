using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Singularity.Services.Karaoke;

namespace Singularity.Views.Avalonia.Controls;

/// <summary>
/// Plays a music video in step with audio that plays elsewhere: each frame it asks <see cref="Clock"/>
/// where the audio is and shows the video frame due there (UltraStar's #VIDEOGAP: video position = audio
/// position + gap). The video is decoded by ffmpeg into a bitmap drawn here, like the sing stage's, so it
/// works under other controls. A jump in the audio (seek, next preview) reopens the video at the new
/// spot. Draws nothing until the first frame, so whatever is behind it (a cover) shows meanwhile.
/// </summary>
public sealed class VideoSurface : Control
{
    public static readonly StyledProperty<string?> VideoPathProperty = AvaloniaProperty.Register<VideoSurface, string?>(nameof(VideoPath));
    public static readonly StyledProperty<double> VideoGapMsProperty = AvaloniaProperty.Register<VideoSurface, double>(nameof(VideoGapMs));
    public static readonly StyledProperty<Func<double?>?> ClockProperty = AvaloniaProperty.Register<VideoSurface, Func<double?>?>(nameof(Clock));
    public static readonly StyledProperty<Stretch> StretchProperty = AvaloniaProperty.Register<VideoSurface, Stretch>(nameof(Stretch), Stretch.UniformToFill);

    /// <summary>Further than this from where the decoder is, the video is reopened at the audio's position.</summary>
    private const double JumpMs = 1500;

    private VideoFrameSource? _source;
    private string? _openPath;
    private double _decoderMs;
    private WriteableBitmap? _bitmap;
    private bool _hasFrame;
    private bool _attached;

    static VideoSurface()
    {
        AffectsRender<VideoSurface>(StretchProperty);
    }

    /// <summary>The video file; null shows nothing.</summary>
    public string? VideoPath { get => GetValue(VideoPathProperty); set => SetValue(VideoPathProperty, value); }

    public double VideoGapMs { get => GetValue(VideoGapMsProperty); set => SetValue(VideoGapMsProperty, value); }

    /// <summary>Where the audio is, in ms; null while nothing plays (the last frame stays).</summary>
    public Func<double?>? Clock { get => GetValue(ClockProperty); set => SetValue(ClockProperty, value); }

    public Stretch Stretch { get => GetValue(StretchProperty); set => SetValue(StretchProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Close();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == VideoPathProperty || change.Property == VideoGapMsProperty)
        {
            Close();
            _hasFrame = false;
            InvalidateVisual();
        }
    }

    private void RequestFrame() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
    {
        if (!_attached) return;
        Advance();
        RequestFrame();
    });

    /// <summary>Before the render pass: takes the due frame into the bitmap.</summary>
    private void Advance()
    {
        var path = VideoPath;
        if (path is null || Clock?.Invoke() is not { } audioMs) return;
        double videoMs = audioMs + VideoGapMs;
        if (videoMs < 0) return;

        if (_source is null || _openPath != path || Math.Abs(videoMs - _decoderMs) > JumpMs)
        {
            Close();
            _source = VideoFrameSource.Open(path, videoMs, NullLogger.Instance);
            _openPath = path;
            _decoderMs = videoMs;
            if (_source is null) return;
        }

        var frame = _source.TakeFrame(videoMs);
        _decoderMs = videoMs;
        if (frame is null) return;
        try
        {
            if (_bitmap is null || _bitmap.PixelSize.Width != _source.Width || _bitmap.PixelSize.Height != _source.Height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(_source.Width, _source.Height), new Vector(96, 96),
                    global::Avalonia.Platform.PixelFormat.Bgra8888, global::Avalonia.Platform.AlphaFormat.Opaque);
            }
            using (var buffer = _bitmap.Lock())
                System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, 0, buffer.Address, frame.Pixels.Length);
            _hasFrame = true;
            InvalidateVisual();
        }
        finally
        {
            _source.Release(frame);
        }
    }

    public override void Render(DrawingContext context)
    {
        if (!_hasFrame || _bitmap is null || VideoPath is null) return;
        var size = _bitmap.Size;
        var bounds = new Rect(Bounds.Size);
        double scale = Stretch == Stretch.Uniform
            ? Math.Min(bounds.Width / size.Width, bounds.Height / size.Height)
            : Math.Max(bounds.Width / size.Width, bounds.Height / size.Height);
        var dest = new Rect((bounds.Width - size.Width * scale) / 2, (bounds.Height - size.Height * scale) / 2, size.Width * scale, size.Height * scale);
        using (context.PushClip(bounds))
            context.DrawImage(_bitmap, new Rect(size), dest);
    }

    private void Close()
    {
        _source?.Dispose();
        _source = null;
        _openPath = null;
    }
}
