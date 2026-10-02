using Singularity.Models;

namespace Singularity.Services;

public interface IWaveformCacheService
{
    float[] GetOrCreateRmsProfile(string cacheKey, WaveformAnalysisData data, int targetBins);
    void Invalidate(string? cacheKey = null);
}
