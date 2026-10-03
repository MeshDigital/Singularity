using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Contracts.Inference;
using Singularity.Karaoke.Library;
using Singularity.Services.Inference;

namespace Singularity.Services.Karaoke;

/// <summary>
/// Removes the vocals from a song for real karaoke: asks the inference worker (Demucs, about 40 s per
/// song on the GPU) for vocals and instrumental stems and stores them in the <see cref="StemStore"/>
/// cache. The worker is started on first use and kept running for the next song.
/// </summary>
public sealed class StemSeparationService : IAsyncDisposable
{
    private readonly StemStore _store;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<StemSeparationService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceWorkerHost? _host;

    public StemSeparationService(StemStore store, ILoggerFactory loggers)
    {
        _store = store;
        _loggers = loggers;
        _logger = loggers.CreateLogger<StemSeparationService>();
    }

    /// <summary>False when the inference worker isn't installed (no inference\.venv found).</summary>
    public bool IsAvailable => InferenceWorkerOptions.Discover() is not null;

    /// <summary>Separates <paramref name="entry"/>'s audio; returns the stems (immediately if they already exist).</summary>
    /// <param name="progress">0..1 as separation proceeds.</param>
    public async Task<(string Vocals, string Instrumental)> SeparateAsync(SongEntry entry, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (_store.Find(entry) is { } existing) return existing;
        if (entry.AudioPath is null) throw new InvalidOperationException("The song has no audio file.");

        await _gate.WaitAsync(ct);
        try
        {
            _host ??= new InferenceWorkerHost(
                InferenceWorkerOptions.Discover() ?? throw new InvalidOperationException("The AI worker isn't installed (inference\\.venv not found)."),
                _loggers.CreateLogger<InferenceWorkerHost>());

            var folder = _store.CacheFolderFor(entry.AudioPath);
            Directory.CreateDirectory(folder);
            var events = new Progress<WorkerEvent>(e =>
            {
                if (e is ProgressUpdateEvent p) progress?.Report(p.Progress);
            });
            _logger.LogInformation("Separating vocals: {Song}", entry.TxtPath);
            await _host.SeparateStemsAsync(new SeparateStemsCommand($"stems-{Guid.NewGuid():N}", entry.AudioPath, folder), events, ct);
            progress?.Report(1);
            return _store.Find(entry) ?? throw new InvalidOperationException("The worker finished but wrote no stems.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is { } host) await host.DisposeAsync();
    }
}
