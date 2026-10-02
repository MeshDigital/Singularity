using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Singularity.Services.Similarity;

namespace Singularity.Services;

public sealed class SimilarityServiceAdapter : ISimilarityService
{
    private readonly SimilarityIndex _index;

    public SimilarityServiceAdapter(SimilarityIndex index)
    {
        _index = index;
    }

    public int IndexSize => _index.IndexSize;

    public Task<IReadOnlyList<SimilarTrack>> GetSimilarTracksAsync(
        string queryHash,
        int topN = 10,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? excludeHashes = null)
        => _index.GetSimilarTracksAsync(queryHash, topN, cancellationToken, excludeHashes);

    public void InvalidateIndex() => _index.InvalidateIndex();
}
