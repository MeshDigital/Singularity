using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Services.AudioAnalysis;

namespace Singularity.Services.Karaoke.Ingest;

/// <summary>Finds and downloads a music video for a song.</summary>
public interface IVideoFinder
{
    /// <summary>Downloads the video (with its sound, which the sync needs) into <paramref name="folder"/>; null when none was found.</summary>
    Task<string?> FindAsync(string artist, string title, int durationMs, string folder, CancellationToken ct);

    /// <summary>Downloads one YouTube video by id (a chart's own); null when it's gone.</summary>
    Task<string?> DownloadAsync(string youtubeId, string folder, CancellationToken ct);
}

/// <summary>
/// Music videos from YouTube via yt-dlp: the first of the top five search results for
/// "{artist} - {title} official music video" whose length fits the song (a video may add an intro
/// or outro, but not halve or double it), as mp4 up to 1080p. Whether it really is the album
/// arrangement is decided afterwards by the video sync.
/// </summary>
public sealed class YtDlpVideoFinder : IVideoFinder
{
    public const string FileStem = "video_source";

    /// <summary>A video up to this much shorter than the song (a trimmed outro) still counts.</summary>
    public const int MaxShorterSeconds = 15;

    /// <summary>A video up to this much longer than the song (a skit, a long intro) still counts.</summary>
    public const int MaxLongerSeconds = 120;

    private readonly ILogger _logger;

    public YtDlpVideoFinder(ILogger logger) => _logger = logger;

    /// <summary>yt-dlp next to the app (Tools\yt-dlp.exe), from winget, or on the PATH; null when it isn't installed.</summary>
    public static string? Locate()
    {
        var name = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, name),
            Path.Combine(AppContext.BaseDirectory, "Tools", name),
            // winget's command aliases; an app started before the install doesn't see the new PATH yet.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", name),
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), name)));
        return candidates.FirstOrDefault(File.Exists);
    }

    public bool IsAvailable => Locate() is not null;

    internal static IReadOnlyList<string> Arguments(string artist, string title, int durationMs, string folder, string? ffmpeg)
    {
        int seconds = durationMs / 1000;
        return VideoArguments(folder, ffmpeg, $"ytsearch5:{artist} - {title} official music video",
            $"!is_live & duration >= {Math.Max(1, seconds - MaxShorterSeconds)} & duration <= {seconds + MaxLongerSeconds}");
    }

    public Task<string?> FindAsync(string artist, string title, int durationMs, string folder, CancellationToken ct) =>
        RunAsync(Arguments(artist, title, durationMs, folder, AudioIngestionPipeline.ResolveFfmpegPath()), $"{artist} - {title}", folder, ct);

    public Task<string?> DownloadAsync(string youtubeId, string folder, CancellationToken ct) =>
        System.Text.RegularExpressions.Regex.IsMatch(youtubeId, "^[A-Za-z0-9_-]{11}$")
            ? RunAsync(VideoArguments(folder, AudioIngestionPipeline.ResolveFfmpegPath(), $"https://www.youtube.com/watch?v={youtubeId}", matchFilter: "!is_live"), youtubeId, folder, ct)
            : Task.FromResult<string?>(null);

    internal static IReadOnlyList<string> VideoArguments(string folder, string? ffmpeg, string target, string matchFilter)
    {
        var args = new List<string>
        {
            "--no-playlist", "--no-progress", "--quiet", "--no-warnings", "--no-mtime",
            "-f", "bestvideo[ext=mp4][height<=1080]+bestaudio[ext=m4a]/best[ext=mp4][height<=1080]/best[height<=1080]/best",
            "--merge-output-format", "mp4",
            "--match-filter", matchFilter,
            "--max-downloads", "1",
            "-o", Path.Combine(folder, FileStem + ".%(ext)s"),
        };
        if (ffmpeg is { Length: > 0 } && File.Exists(ffmpeg)) args.AddRange(new[] { "--ffmpeg-location", ffmpeg });
        args.Add(target);
        return args;
    }

    private async Task<string?> RunAsync(IReadOnlyList<string> arguments, string what, string folder, CancellationToken ct)
    {
        var exe = Locate();
        if (exe is null) return null;

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start yt-dlp.");
        using var kill = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(ct);
        var errorText = (await errors).Trim();
        await output;

        // Exit code 101: stopped after --max-downloads, i.e. success.
        var file = Directory.GetFiles(folder, FileStem + ".*").FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            if (process.ExitCode is not (0 or 101))
                _logger.LogWarning("yt-dlp found no video for {What} (exit {Code}): {Error}", what, process.ExitCode, errorText);
            else
                _logger.LogInformation("No music video of a fitting length for {What}", what);
            return null;
        }
        _logger.LogInformation("Downloaded a music video for {What} ({MB} MB) in {Seconds:0} s",
            what, (new FileInfo(file).Length / 1_048_576.0).ToString("0", CultureInfo.InvariantCulture), sw.Elapsed.TotalSeconds);
        return file;
    }
}
