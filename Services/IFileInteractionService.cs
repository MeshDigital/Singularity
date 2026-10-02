using System.Collections.Generic;
using System.Threading.Tasks;

namespace Singularity.Services;

public record FileDialogFilter(string Name, List<string> Extensions);

public interface IFileInteractionService
{
    Task<string?> OpenFolderDialogAsync(string title);
    Task<string?> OpenFileDialogAsync(string title, IEnumerable<FileDialogFilter>? filters = null);
    Task<string?> SaveFileDialogAsync(string title, string defaultFileName, string? extension = null);
    void RevealFileInExplorer(string filePath);

}
