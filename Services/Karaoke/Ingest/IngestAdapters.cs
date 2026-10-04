using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Singularity.Contracts.Inference;
using Singularity.Services.AudioAnalysis;
using Singularity.Services.Lyrics;

namespace Singularity.Services.Karaoke.Ingest;

/// <summary>LRCLIB lyrics for the packager: synced when they fit the recording, else plain words.</summary>
public sealed class LrclibLyricsLookup(LrclibClient client) : ILyricsLookup
{
    public async Task<(string Text, LyricsKind Kind)?> FindAsync(string artist, string title, string? album, int durationMs, CancellationToken ct) =>
        (await client.FindAsync(artist, title, album, durationMs, ct))?.ForWorker();
}

/// <summary>The packager's media work with ffmpeg/ffprobe, and cover downloads over HTTP.</summary>
public sealed class FfmpegIngestMedia(HttpClient http) : IIngestMedia
{
    private static string Ffmpeg => AudioIngestionPipeline.ResolveFfmpegPath();

    /// <summary>ffprobe next to ffmpeg, else on the PATH.</summary>
    private static string Ffprobe
    {
        get
        {
            var dir = Path.GetDirectoryName(Ffmpeg);
            var name = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
            return dir is { Length: > 0 } && File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : name;
        }
    }

    public async Task<int> ProbeDurationMsAsync(string path, CancellationToken ct)
    {
        var (output, _) = await RunAsync(Ffprobe, new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path }, ct);
        var text = System.Text.Encoding.ASCII.GetString(output).Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? (int)Math.Round(seconds * 1000)
            : throw new InvalidOperationException($"Couldn't read the length of {Path.GetFileName(path)}.");
    }

    public async Task<float[]> DecodeMonoAsync(string path, int sampleRate, CancellationToken ct)
    {
        var (output, _) = await RunAsync(Ffmpeg,
            new[] { "-v", "error", "-nostdin", "-i", path, "-vn", "-f", "f32le", "-ac", "1", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-" },
            ct, checkExit: false); // a video without an audio track exits with an error and no samples
        var samples = new float[output.Length / 4];
        Buffer.BlockCopy(output, 0, samples, 0, samples.Length * 4);
        return samples;
    }

    public Task StripAudioAsync(string videoIn, string videoOut, CancellationToken ct) =>
        RunAsync(Ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-i", videoIn, "-map", "0:v:0", "-c", "copy", "-an", videoOut }, ct);

    public async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
    }

    private static async Task<(byte[] Output, string Errors)> RunAsync(string exe, string[] args, CancellationToken ct, bool checkExit = true)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {exe}.");
        using var kill = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        var errors = process.StandardError.ReadToEndAsync(ct);
        using var buffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
        await process.WaitForExitAsync(ct);
        var errorText = await errors;
        if (checkExit && process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileNameWithoutExtension(exe)} failed: {errorText.Trim()}");
        return (buffer.ToArray(), errorText);
    }
}
