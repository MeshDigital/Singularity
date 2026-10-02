using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Singularity.Models.Stem;

namespace Singularity.Services;

public interface IStemSeparationService
{
    string Name { get; }
    bool IsAvailable { get; }
    string ModelTag { get; }

    Task<Dictionary<StemType, string>> SeparateWithProgressAsync(
        string inputFilePath,
        string outputDirectory,
        IProgress<float>? progress,
        CancellationToken cancellationToken = default);
}
