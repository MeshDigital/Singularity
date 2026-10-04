using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Contracts.Song;

namespace Singularity.Services.Karaoke.Ingest;

/// <summary>Where a song being imported is: first ORBIT's download, then the AI.</summary>
public enum IngestState
{
    /// <summary>Handed to the downloader, not picked up yet.</summary>
    Waiting,
    Searching,
    Downloading,
    /// <summary>Downloaded; waiting for its turn with the AI.</summary>
    InQueue,
    Building,
    Ready,
    Failed,
}

/// <summary>One song in the import list, as the queue table shows it.</summary>
/// <param name="Key">Identifies the song across download events (ORBIT's track hash).</param>
/// <param name="Stage">While <see cref="IngestState.Building"/>: what the packager is doing.</param>
/// <param name="Detail">A failure, or what the finished song lacks (no video, …).</param>
public sealed record IngestItem(
    string Key,
    string Artist,
    string Title,
    IngestState State,
    IngestStage? Stage = null,
    double Progress = 0,
    string? Detail = null,
    string? PackageFolder = null,
    QualityTier? Tier = null);

/// <summary>
/// The import list and its AI step: songs are added as their download starts, and each downloaded one
/// is turned into a song folder by the <see cref="KaraokePackager"/>, one at a time (the AI uses the
/// GPU). While someone sings the queue is held: the song being built is stopped and starts over
/// afterwards, so the game keeps the GPU to itself.
/// </summary>
public sealed class IngestQueue
{
    private readonly KaraokePackager _packager;
    private readonly Func<string> _outputRoot;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly List<IngestItem> _items = new();
    private readonly LinkedList<(string Key, IngestSource Source)> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private CancellationTokenSource? _current;
    private bool _held;
    private Task? _pump;

    /// <param name="outputRoot">The song folder imports go to, read when each package is published.</param>
    public IngestQueue(KaraokePackager packager, Func<string> outputRoot, ILogger logger)
    {
        _packager = packager;
        _outputRoot = outputRoot;
        _logger = logger;
    }

    /// <summary>Raised (on a background thread) whenever an item changes.</summary>
    public event Action? Changed;

    /// <summary>A song folder was finished and moved into the library.</summary>
    public event Action<string>? PackageReady;

    public IReadOnlyList<IngestItem> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public bool IsHeld
    {
        get { lock (_lock) return _held; }
    }

    /// <summary>Adds a song to the list, or updates where its download is. A song already being built or done stays as it is.</summary>
    public void Track(string key, string artist, string title, IngestState state = IngestState.Waiting, string? detail = null)
    {
        lock (_lock)
        {
            int i = _items.FindIndex(x => x.Key == key);
            if (i < 0) _items.Add(new IngestItem(key, artist, title, state, Detail: detail));
            else if (_items[i].State is not (IngestState.InQueue or IngestState.Building or IngestState.Ready))
                _items[i] = _items[i] with { State = state, Detail = detail };
            else return;
        }
        Changed?.Invoke();
    }

    /// <summary>The download is there: queue it for the AI.</summary>
    public void Enqueue(string key, IngestSource source)
    {
        lock (_lock)
        {
            if (_pending.Any(p => p.Key == key)) return;
            int i = _items.FindIndex(x => x.Key == key);
            if (i >= 0 && _items[i].State is IngestState.Building or IngestState.Ready) return;
            var item = new IngestItem(key, source.Artist, source.Title, IngestState.InQueue);
            if (i < 0) _items.Add(item);
            else _items[i] = item;
            _pending.AddLast((key, source));
            _pump ??= Task.Run(PumpAsync);
        }
        _signal.Release();
        Changed?.Invoke();
    }

    /// <summary>Stops the song being built (it starts over on <see cref="Release"/>) and starts no new one.</summary>
    public void Hold()
    {
        lock (_lock)
        {
            _held = true;
            _current?.Cancel();
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (!_held) return;
            _held = false;
        }
        _signal.Release();
    }

    /// <summary>Completes when nothing is pending or being built (for tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            lock (_lock)
            {
                if (_pending.Count == 0 && _current is null) return;
            }
            await Task.Delay(10);
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            await _signal.WaitAsync();
            while (TryTake(out var key, out var source, out var cts))
            {
                await BuildAsync(key, source, cts);
            }
        }
    }

    private bool TryTake(out string key, out IngestSource source, out CancellationTokenSource cts)
    {
        lock (_lock)
        {
            if (_held || _pending.First is not { } first)
            {
                (key, source, cts) = (null!, null!, null!);
                return false;
            }
            _pending.RemoveFirst();
            (key, source) = first.Value;
            cts = _current = new CancellationTokenSource();
            Update(key, x => x with { State = IngestState.Building, Stage = IngestStage.Preparing, Progress = 0, Detail = null });
            return true;
        }
    }

    private async Task BuildAsync(string key, IngestSource source, CancellationTokenSource cts)
    {
        var progress = new InlineProgress(p => Update(key, x => x.State == IngestState.Building ? x with { Stage = p.Stage, Progress = p.Fraction } : x));
        try
        {
            var result = await _packager.BuildAsync(source, _outputRoot(), progress, cts.Token);
            Update(key, x => x with
            {
                State = IngestState.Ready, Stage = null, Progress = 1, PackageFolder = result.PackageFolder,
                Tier = result.Quality.Tier, Detail = result.Notes.Count > 0 ? string.Join(" ", result.Notes) : null,
            });
            PackageReady?.Invoke(result.PackageFolder);
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            // Held for singing (however the stopped stage reports it): back to the front of the line.
            lock (_lock) _pending.AddFirst((key, source));
            Update(key, x => x with { State = IngestState.InQueue, Stage = null, Progress = 0 });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ingest failed for {Artist} - {Title}", source.Artist, source.Title);
            Update(key, x => x with { State = IngestState.Failed, Stage = null, Detail = ex.Message });
        }
        finally
        {
            lock (_lock)
            {
                if (_current == cts) _current = null;
            }
            cts.Dispose();
        }
    }

    private void Update(string key, Func<IngestItem, IngestItem> change)
    {
        lock (_lock)
        {
            int i = _items.FindIndex(x => x.Key == key);
            if (i < 0) return;
            _items[i] = change(_items[i]);
        }
        Changed?.Invoke();
    }

    private sealed class InlineProgress(Action<IngestProgress> report) : IProgress<IngestProgress>
    {
        public void Report(IngestProgress value) => report(value);
    }
}
