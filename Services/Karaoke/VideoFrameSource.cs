using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Singularity.Services.AudioAnalysis;

namespace Singularity.Services.Karaoke;

/// <summary>One decoded video frame: BGRA pixels, and when in the video it belongs.</summary>
public sealed class VideoFrame
{
    public VideoFrame(int width, int height) => Pixels = new byte[width * height * 4];
    public byte[] Pixels { get; }
    public double TimeMs { get; set; }
}

/// <summary>
/// Decodes a song's video for the sing screen as raw BGRA frames from ffmpeg (already a dependency;
/// handles avi/divx/flv/mp4 alike) at the source's own width up to <see cref="MaxWidth"/> (1080p), at a
/// constant <see cref="Fps"/>; smaller videos are not upscaled here, the stage scales them when drawing.
/// A reader thread keeps up to <see cref="QueueDepth"/> frames ahead; the stage asks for the frame at
/// the current video time and is handed the newest one that is due, so video follows the audio clock
/// and a slow frame is skipped rather than shown late. The video is drawn into the Skia scene, never
/// as a native window, which would paint over the notes.
/// </summary>
public sealed class VideoFrameSource : IDisposable
{
    public const int MaxWidth = 1920;
    public const double Fps = 30;
    private const int QueueDepth = 4;

    private readonly ILogger _logger;
    private readonly Process _ffmpeg;
    private readonly Thread _reader;
    private readonly Queue<VideoFrame> _ready = new();
    private readonly Stack<VideoFrame> _free = new();
    private readonly object _sync = new();
    private readonly double _startMs;
    private volatile bool _stopped;

    public int Width { get; }
    public int Height { get; }

    private VideoFrameSource(ILogger logger, Process ffmpeg, int width, int height, double startMs)
    {
        Width = width;
        _logger = logger;
        _ffmpeg = ffmpeg;
        Height = height;
        _startMs = startMs;
        for (int i = 0; i < QueueDepth + 2; i++) _free.Push(new VideoFrame(width, height));
        _reader = new Thread(ReadFrames) { IsBackground = true, Name = "video-frames" };
        _reader.Start();
    }

    /// <summary>Starts decoding <paramref name="path"/> at <paramref name="startMs"/> into the video. Null when it can't be opened.</summary>
    public static VideoFrameSource? Open(string path, double startMs, ILogger logger)
    {
        try
        {
            var ffmpeg = AudioIngestionPipeline.ResolveFfmpegPath();
            var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase));
            if (!File.Exists(ffprobe)) ffprobe = "ffprobe";

            var (w, h) = ProbeSize(ffprobe, path);
            if (w <= 0 || h <= 0) return null;
            int width = Math.Min(MaxWidth, w / 2 * 2);
            int height = Math.Max(2, (int)Math.Round(width * (double)h / w / 2) * 2);

            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[]
                     {
                         "-v", "error", "-nostdin",
                         "-ss", (Math.Max(0, startMs) / 1000).ToString("0.###", CultureInfo.InvariantCulture),
                         "-i", path, "-an", "-sn",
                         "-vf", $"fps={Fps.ToString(CultureInfo.InvariantCulture)},scale={width}:{height}",
                         "-f", "rawvideo", "-pix_fmt", "bgra", "-",
                     })
                psi.ArgumentList.Add(a);
            var process = Process.Start(psi)!;
            process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) logger.LogDebug("[ffmpeg video] {Line}", e.Data); };
            process.BeginErrorReadLine();
            logger.LogInformation("Video: {File} {W}x{H} → {OutW}x{OutH} @ {Fps} fps from {Start:0} ms", Path.GetFileName(path), w, h, width, height, Fps, startMs);
            return new VideoFrameSource(logger, process, width, height, Math.Max(0, startMs));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Video {Path} could not be opened; showing the background instead", path);
            return null;
        }
    }

    private static (int W, int H) ProbeSize(string ffprobe, string path)
    {
        var psi = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(5000);
        var parts = text.Split('x');
        return parts.Length >= 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) ? (w, h) : (0, 0);
    }

    private void ReadFrames()
    {
        var stream = _ffmpeg.StandardOutput.BaseStream;
        long index = 0;
        try
        {
            while (!_stopped)
            {
                VideoFrame frame;
                lock (_sync)
                {
                    while (_free.Count == 0 && !_stopped) Monitor.Wait(_sync); // queue full: wait for the stage
                    if (_stopped) return;
                    frame = _free.Pop();
                }

                int read = 0;
                while (read < frame.Pixels.Length)
                {
                    int n = stream.Read(frame.Pixels, read, frame.Pixels.Length - read);
                    if (n == 0) return; // end of video
                    read += n;
                }
                frame.TimeMs = _startMs + index++ * 1000.0 / Fps;
                lock (_sync) _ready.Enqueue(frame);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (!_stopped) _logger.LogDebug(ex, "Video frame reader stopped");
        }
    }

    /// <summary>
    /// The newest decoded frame due at <paramref name="videoTimeMs"/>, or null when none is due yet.
    /// Hand it back with <see cref="Release"/> once drawn.
    /// </summary>
    public VideoFrame? TakeFrame(double videoTimeMs)
    {
        lock (_sync)
        {
            VideoFrame? due = null;
            while (_ready.Count > 0 && _ready.Peek().TimeMs <= videoTimeMs)
            {
                if (due is not null) _free.Push(due); // skipped: we're behind
                due = _ready.Dequeue();
            }
            if (due is not null) Monitor.PulseAll(_sync);
            return due;
        }
    }

    public void Release(VideoFrame frame)
    {
        lock (_sync)
        {
            _free.Push(frame);
            Monitor.PulseAll(_sync);
        }
    }

    public void Dispose()
    {
        _stopped = true;
        lock (_sync) Monitor.PulseAll(_sync);
        try { if (!_ffmpeg.HasExited) _ffmpeg.Kill(); } catch (InvalidOperationException) { }
        _ffmpeg.Dispose();
    }
}
