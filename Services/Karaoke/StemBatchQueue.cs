using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Karaoke.Library;

namespace Singularity.Services.Karaoke;

/// <summary>What the batch queue needs from vocal separation; implemented by <see cref="StemSeparationService"/>.</summary>
public interface IStemSeparator
{
    bool HasStems(SongEntry entry);
    Task SeparateAsync(SongEntry entry, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>A point-in-time view of the queue, for the progress line in song select.</summary>
public sealed record StemBatchStatus(
    bool IsRunning, bool IsHeld, bool IsPaused, int Total, int Done, int Skipped, int Failed, string? Current, TimeSpan? Remaining)
{
    public int Finished => Done + Skipped + Failed;
}

/// <summary>
/// Removes the vocals from a whole collection in the background, one song at a time, using the GPU
/// for hours.
/// <list type="bullet">
/// <item>Resumable: songs whose stems already exist are skipped, so stopping and starting again
/// carries on where it was.</item>
/// <item>Out of the way: <see cref="Hold"/> (called when a song starts) abandons the track in progress
/// and waits until <see cref="Release"/>, so separation never competes with singing for the GPU; the
/// abandoned track is redone later. The user can also pause it.</item>
/// <item>Cool: a configurable rest between songs lets a laptop GPU shed heat; the worker runs below
/// normal priority and frees GPU memory after every song.</item>
/// <item>Isolated: a song that fails is logged to batch_errors.log and skipped; the rest carries on.</item>
/// </list>
/// </summary>
public sealed class StemBatchQueue
{
    private readonly IStemSeparator _separator;
    private readonly ILogger<StemBatchQueue> _logger;
    private readonly string _errorLog;
    private readonly object _sync = new();
    private readonly List<TimeSpan> _durations = new();

    private CancellationTokenSource? _run;
    private CancellationTokenSource? _track;
    private TaskCompletionSource _resume = NewResume(completed: true);
    private bool _held, _paused;
    private int _total, _done, _skipped, _failed;
    private string? _current;

    public StemBatchQueue(IStemSeparator separator, ILogger<StemBatchQueue> logger, string? errorLogPath = null)
    {
        _separator = separator;
        _logger = logger;
        _errorLog = errorLogPath ?? Path.Combine(
            AppContext.GetData("Singularity.LogDirectory") as string
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Singularity", "logs"),
            "batch_errors.log");
    }

    /// <summary>Rest between songs, so a laptop GPU can cool down.</summary>
    public TimeSpan RestBetweenSongs { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Raised (on a worker thread) whenever the status changes.</summary>
    public event Action<StemBatchStatus>? Changed;

    public string ErrorLogPath => _errorLog;

    public StemBatchStatus Status
    {
        get
        {
            lock (_sync)
            {
                int left = _total - _done - _skipped - _failed;
                TimeSpan? remaining = _durations.Count == 0 || left <= 0
                    ? null
                    : TimeSpan.FromTicks((long)(_durations.Average(d => d.Ticks) * left)) + RestBetweenSongs * left;
                return new StemBatchStatus(_run is not null, _held, _paused, _total, _done, _skipped, _failed, _current, remaining);
            }
        }
    }

    /// <summary>
    /// Starts removing vocals for <paramref name="songs"/> in the background and returns at once (the
    /// batch runs for hours; follow it through <see cref="Changed"/>). Does nothing while a batch is running.
    /// </summary>
    public void Start(IEnumerable<SongEntry> songs)
    {
        CancellationTokenSource run;
        List<SongEntry> list;
        lock (_sync)
        {
            if (_run is not null) return;
            list = songs.Where(s => s.IsPlayable).ToList();
            _run = run = new CancellationTokenSource();
            _total = list.Count;
            _done = _skipped = _failed = 0;
            _durations.Clear();
            _paused = false;
        }
        Raise();
        _ = Task.Run(() => RunAsync(list, run.Token));
    }

    /// <summary>Stops the batch; the song in progress is abandoned (and redone by the next batch).</summary>
    public void Stop()
    {
        lock (_sync)
        {
            _run?.Cancel();
            _track?.Cancel();
            _resume.TrySetResult();
        }
    }

    /// <summary>User pause / resume.</summary>
    public void SetPaused(bool paused)
    {
        lock (_sync)
        {
            _paused = paused;
            if (paused) _track?.Cancel();
            UpdateResume();
        }
        Raise();
    }

    /// <summary>Someone is singing: give them the GPU. The song in progress is abandoned and redone later.</summary>
    public void Hold()
    {
        lock (_sync)
        {
            _held = true;
            _track?.Cancel();
            UpdateResume();
        }
        Raise();
    }

    /// <summary>Singing ended: carry on.</summary>
    public void Release()
    {
        lock (_sync)
        {
            _held = false;
            UpdateResume();
        }
        Raise();
    }

    private void UpdateResume()
    {
        // Called under _sync.
        if (_held || _paused)
        {
            if (_resume.Task.IsCompleted) _resume = NewResume(completed: false);
        }
        else
        {
            _resume.TrySetResult();
        }
    }

    private async Task RunAsync(List<SongEntry> songs, CancellationToken runToken)
    {
        _logger.LogInformation("Batch vocal removal: {Count} songs", songs.Count);
        // Runs for hours, typically overnight: keep the PC from sleeping (the screen may still go off).
        using var awake = KeepAwake.Start("Singularity is removing vocals from songs");
        try
        {
            foreach (var song in songs)
            {
                while (true) // a held or paused track is retried once work may continue
                {
                    Task resume;
                    lock (_sync) resume = _resume.Task;
                    await resume.WaitAsync(runToken);
                    runToken.ThrowIfCancellationRequested();

                    if (_separator.HasStems(song))
                    {
                        lock (_sync) _skipped++;
                        break;
                    }

                    CancellationTokenSource track;
                    lock (_sync)
                    {
                        track = _track = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                        _current = $"{song.Song.Artist} - {song.Song.Title}";
                    }
                    Raise();

                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await _separator.SeparateAsync(song, null, track.Token);
                        lock (_sync)
                        {
                            _done++;
                            _durations.Add(sw.Elapsed);
                        }
                        break;
                    }
                    catch (OperationCanceledException) when (!runToken.IsCancellationRequested)
                    {
                        // Held or paused mid-song: loop round and redo it when work may continue.
                        _logger.LogInformation("Batch vocal removal: {Song} interrupted, will redo it", _current);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        lock (_sync) _failed++;
                        LogFailure(song, ex);
                        break;
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            _track = null;
                            _current = null;
                        }
                        track.Dispose();
                    }
                }
                Raise();
                if (RestBetweenSongs > TimeSpan.Zero) await Task.Delay(RestBetweenSongs, runToken);
            }
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            _logger.LogInformation("Batch vocal removal stopped");
        }
        finally
        {
            lock (_sync)
            {
                _run?.Dispose();
                _run = null;
                _current = null;
            }
            var s = Status;
            _logger.LogInformation("Batch vocal removal finished: {Done} separated, {Skipped} already done, {Failed} failed", s.Done, s.Skipped, s.Failed);
            Raise();
        }
    }

    private void LogFailure(SongEntry song, Exception ex)
    {
        _logger.LogWarning(ex, "Batch vocal removal failed for {Song}", song.TxtPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_errorLog)!);
            File.AppendAllText(_errorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{song.TxtPath}\t{ex.GetType().Name}: {ex.Message.Replace('\n', ' ')}{Environment.NewLine}");
        }
        catch (IOException logEx)
        {
            _logger.LogDebug(logEx, "Could not write {Log}", _errorLog);
        }
    }

    private void Raise() => Changed?.Invoke(Status);

    private static TaskCompletionSource NewResume(bool completed)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) tcs.SetResult();
        return tcs;
    }
}
