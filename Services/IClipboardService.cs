using System.Threading.Tasks;

namespace Singularity.Services;

public interface IClipboardService
{
    Task<string?> GetTextAsync();
    Task SetTextAsync(string text);
}
