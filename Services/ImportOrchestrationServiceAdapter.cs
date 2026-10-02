using System.Threading.Tasks;
using Singularity.Models;

namespace Singularity.Services;

public sealed class ImportOrchestrationServiceAdapter : IImportOrchestrationService
{
    private readonly ImportOrchestrator _inner;

    public ImportOrchestrationServiceAdapter(ImportOrchestrator inner)
    {
        _inner = inner;
    }

    public Task StartImportWithPreviewAsync(IImportProvider provider, string input)
        => _inner.StartImportWithPreviewAsync(provider, input);

    public Task SilentImportAsync(IImportProvider provider, string input)
        => _inner.SilentImportAsync(provider, input);
}
