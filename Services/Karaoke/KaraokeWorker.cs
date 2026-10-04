using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Contracts.Inference;
using Singularity.Services.Inference;

namespace Singularity.Services.Karaoke;

/// <summary>
/// The one inference worker the karaoke side shares: vocal removal and chart generation both go
/// through it, one task at a time, so two copies of the models never compete for GPU memory. The
/// worker is started on first use and kept running for the next task.
/// </summary>
public sealed class KaraokeWorker : Ingest.ITrackAnalyzer, IAsyncDisposable
{
    private readonly ILoggerFactory _loggers;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceWorkerHost? _host;

    public KaraokeWorker(ILoggerFactory loggers) => _loggers = loggers;

    /// <summary>False when the inference worker isn't installed (no inference\.venv found).</summary>
    public bool IsAvailable => InferenceWorkerOptions.Discover() is not null;

    public Task SeparateStemsAsync(SeparateStemsCommand command, IProgress<WorkerEvent>? progress, CancellationToken ct) =>
        RunAsync(host => host.SeparateStemsAsync(command, progress, ct), ct);

    public async Task<TrackAnalysisResult> ProcessTrackAsync(ProcessTrackCommand command, IProgress<WorkerEvent>? progress, CancellationToken ct)
    {
        TrackAnalysisResult? result = null;
        await RunAsync(async host => result = await host.ProcessTrackAsync(command, progress, ct), ct);
        return result!;
    }

    Task<TrackAnalysisResult> Ingest.ITrackAnalyzer.AnalyzeAsync(ProcessTrackCommand command, IProgress<WorkerEvent>? progress, CancellationToken ct) =>
        ProcessTrackAsync(command, progress, ct);

    Task Ingest.ITrackAnalyzer.SeparateAsync(SeparateStemsCommand command, IProgress<WorkerEvent>? progress, CancellationToken ct) =>
        SeparateStemsAsync(command, progress, ct);

    /// <summary>
    /// Songs one worker process handles before it is replaced. Its resident memory creeps up by about
    /// 0.6 GB over 50 songs (measured), which an overnight batch of the whole collection would pile up;
    /// a fresh process costs a few seconds.
    /// </summary>
    public const int TasksPerWorker = 25;

    private int _tasks;

    private async Task RunAsync(Func<InferenceWorkerHost, Task> task, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_host is not null && _tasks >= TasksPerWorker)
            {
                await _host.DisposeAsync();
                _host = null;
            }
            if (_host is null)
            {
                _host = CreateHost();
                _tasks = 0;
            }
            _tasks++;
            await task(_host);
        }
        finally
        {
            _gate.Release();
        }
    }

    private InferenceWorkerHost CreateHost()
    {
        var options = InferenceWorkerOptions.Discover()
                      ?? throw new InvalidOperationException("The AI worker isn't installed (inference\\.venv not found).");
        // Karaoke AI work runs in the background: the worker runs below normal priority so the game and
        // the desktop stay responsive (processes it starts inherit that).
        // Demucs can't stop mid-song, so waiting for a cancelled task only keeps the GPU busy while
        // someone sings; the track is redone later anyway. Kill it after one second instead.
        return new InferenceWorkerHost(
            options with
            {
                Environment = new Dictionary<string, string> { ["SINGULARITY_WORKER_PRIORITY"] = "below_normal" },
                CancelGracePeriod = TimeSpan.FromSeconds(1),
            },
            _loggers.CreateLogger<InferenceWorkerHost>());
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is { } host) await host.DisposeAsync();
    }
}
