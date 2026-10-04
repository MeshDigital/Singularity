using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Contracts.Inference;
using Singularity.Karaoke.Library;

namespace Singularity.Services.Karaoke;

/// <summary>
/// Removes the vocals from a song for real karaoke: asks the shared <see cref="KaraokeWorker"/> (Demucs,
/// about 40 s per song on the GPU) for vocals and instrumental stems and stores them in the
/// <see cref="StemStore"/> cache.
/// </summary>
public sealed class StemSeparationService : IStemSeparator
{
    private readonly StemStore _store;
    private readonly KaraokeWorker _worker;
    private readonly ILogger<StemSeparationService> _logger;

    public StemSeparationService(StemStore store, KaraokeWorker worker, ILogger<StemSeparationService> logger)
    {
        _store = store;
        _worker = worker;
        _logger = logger;
    }

    public bool HasStems(SongEntry entry) => _store.Find(entry) is not null;

    async Task IStemSeparator.SeparateAsync(SongEntry entry, IProgress<double>? progress, CancellationToken ct) =>
        await SeparateAsync(entry, progress, ct);

    /// <summary>False when the inference worker isn't installed (no inference\.venv found).</summary>
    public bool IsAvailable => _worker.IsAvailable;

    /// <summary>Separates <paramref name="entry"/>'s audio; returns the stems (immediately if they already exist).</summary>
    /// <param name="progress">0..1 as separation proceeds.</param>
    public async Task<(string Vocals, string Instrumental)> SeparateAsync(SongEntry entry, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (_store.Find(entry) is { } existing) return existing;
        if (entry.AudioPath is null) throw new InvalidOperationException("The song has no audio file.");

        var folder = _store.CacheFolderFor(entry.AudioPath);
        Directory.CreateDirectory(folder);
        var events = new Progress<WorkerEvent>(e =>
        {
            if (e is ProgressUpdateEvent p) progress?.Report(p.Progress);
        });
        _logger.LogInformation("Separating vocals: {Song}", entry.TxtPath);
        await _worker.SeparateStemsAsync(new SeparateStemsCommand($"stems-{Guid.NewGuid():N}", entry.AudioPath, folder), events, ct);
        progress?.Report(1);
        return _store.Find(entry) ?? throw new InvalidOperationException("The worker finished but wrote no stems.");
    }
}
