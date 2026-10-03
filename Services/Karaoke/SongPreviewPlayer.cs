using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Singularity.Karaoke.Library;
using Singularity.Services.Audio;

namespace Singularity.Services.Karaoke;

/// <summary>
/// Song-select previews: once a song has stayed highlighted for <see cref="Delay"/>, its audio fades in
/// at the preview point and plays for up to <see cref="Length"/>; highlighting another song or leaving
/// the page fades it out. The preview point is the chart's #PREVIEWSTART, else the medley section
/// (usually the chorus), else a third of the way in.
/// </summary>
public sealed class SongPreviewPlayer : IDisposable
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan Length = TimeSpan.FromSeconds(30);
    private const int FadeInMs = 800;
    private const int FadeOutMs = 300;

    private readonly ILogger<SongPreviewPlayer> _logger;
    private readonly object _sync = new();
    private CancellationTokenSource? _pending;
    private (WasapiOut Output, AudioFileReader Reader, FadeInOutSampleProvider Fade)? _current;

    public SongPreviewPlayer(ILogger<SongPreviewPlayer> logger) => _logger = logger;

    /// <summary>Highlights <paramref name="entry"/>: stops what's playing and starts this preview after the delay.</summary>
    public void Preview(SongEntry? entry)
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            _pending?.Cancel();
            _pending = cts = new CancellationTokenSource();
        }
        FadeOutCurrent();
        if (entry?.AudioPath is null) return;
        _ = StartAfterDelayAsync(entry, cts.Token);
    }

    public void Stop()
    {
        lock (_sync) _pending?.Cancel();
        FadeOutCurrent();
    }

    /// <summary>Where a preview starts, in ms.</summary>
    public static double PreviewStartMs(SongEntry entry, double durationMs)
    {
        var song = entry.Song;
        if (song.PreviewStartMs is { } preview) return preview;
        if (MedleyFinder.Find(song) is { } medley) return Math.Max(0, song.BeatToMs(medley.StartBeat));
        return durationMs / 3;
    }

    private async Task StartAfterDelayAsync(SongEntry entry, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Delay, ct);
            var reader = await Task.Run(() => PlayableAudio.Open(entry.AudioPath!, out _), ct);
            double start = PreviewStartMs(entry, reader.TotalTime.TotalMilliseconds);
            reader.CurrentTime = TimeSpan.FromMilliseconds(Math.Min(start, Math.Max(0, reader.TotalTime.TotalMilliseconds - 5000)));
            var fade = new FadeInOutSampleProvider(reader, initiallySilent: true);
            var output = new WasapiOut(AudioClientShareMode.Shared, 100);
            output.Init(fade);

            lock (_sync)
            {
                if (ct.IsCancellationRequested)
                {
                    output.Dispose();
                    reader.Dispose();
                    return;
                }
                _current = (output, reader, fade);
            }
            fade.BeginFadeIn(FadeInMs);
            output.Play();

            await Task.Delay(Length, ct);
            FadeOutCurrent();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Preview of {Song} failed", entry.TxtPath);
        }
    }

    private void FadeOutCurrent()
    {
        (WasapiOut Output, AudioFileReader Reader, FadeInOutSampleProvider Fade)? current;
        lock (_sync)
        {
            current = _current;
            _current = null;
        }
        if (current is not { } c) return;
        c.Fade.BeginFadeOut(FadeOutMs);
        _ = Task.Delay(FadeOutMs + 50).ContinueWith(_ =>
        {
            c.Output.Stop();
            c.Output.Dispose();
            c.Reader.Dispose();
        }, TaskScheduler.Default);
    }

    public void Dispose() => Stop();
}
