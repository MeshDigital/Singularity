using System;
using System.Collections.Concurrent;
using Singularity.Models;

namespace Singularity.Services;

/// <summary>
/// Caches downsampled waveform RMS profiles per track key and target bin count.
/// </summary>
public sealed class WaveformCacheService : IWaveformCacheService
{
    private readonly ConcurrentDictionary<string, float[]> _cache = new(StringComparer.Ordinal);

    public float[] GetOrCreateRmsProfile(string cacheKey, WaveformAnalysisData data, int targetBins)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            // No stable identity: build on demand with no long-term caching.
            return ComputeRmsProfile(data?.RmsData ?? Array.Empty<byte>(), targetBins);
        }

        var key = $"{cacheKey}:{Math.Max(1, targetBins)}";
        return _cache.GetOrAdd(key, _ =>
            ComputeRmsProfile(data?.RmsData ?? Array.Empty<byte>(), targetBins));
    }

    public void Invalidate(string? cacheKey = null)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            _cache.Clear();
            return;
        }

        var prefix = $"{cacheKey}:";
        foreach (var key in _cache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Downsamples a raw RMS byte array (values 0–255, one byte per stored
    /// time window) to exactly <paramref name="targetBins"/> float values
    /// in [0, 1].  If <paramref name="sourceBytes"/> is shorter than
    /// <paramref name="targetBins"/>, the available data is interpolated.
    /// </summary>
    public static float[] ComputeRmsProfile(byte[] sourceBytes, int targetBins)
    {
        if (sourceBytes is null || sourceBytes.Length == 0 || targetBins <= 0)
            return Array.Empty<float>();

        var profile = new float[targetBins];
        double ratio = (double)sourceBytes.Length / targetBins;

        for (int i = 0; i < targetBins; i++)
        {
            int srcStart = (int)(i * ratio);
            int srcEnd = Math.Min((int)((i + 1) * ratio), sourceBytes.Length);
            if (srcEnd <= srcStart) srcEnd = srcStart + 1;

            float sum = 0f;
            int count = 0;
            for (int j = srcStart; j < srcEnd && j < sourceBytes.Length; j++)
            {
                sum += sourceBytes[j];
                count++;
            }
            profile[i] = count > 0 ? sum / (count * 255f) : 0f;
        }

        return profile;
    }

    // ── Rendering ─────────────────────────────────────────────────────────
}
