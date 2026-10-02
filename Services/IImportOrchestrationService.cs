using System.Threading.Tasks;
using Singularity.Models;

namespace Singularity.Services;

public interface IImportOrchestrationService
{
    Task StartImportWithPreviewAsync(IImportProvider provider, string input);
    Task SilentImportAsync(IImportProvider provider, string input);
}
