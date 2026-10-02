using System;
using System.Threading.Tasks;
using Singularity.Data.Entities;

namespace Singularity.Services.Repositories;

public interface IEnrichmentTaskRepository
{
    Task QueueTaskAsync(string trackId, Guid? albumId = null);
    Task<EnrichmentTaskEntity?> GetNextPendingTaskAsync();
    Task MarkProcessingAsync(Guid taskId);
    Task MarkCompletedAsync(Guid taskId);
    Task MarkFailedAsync(Guid taskId, string error);
}
