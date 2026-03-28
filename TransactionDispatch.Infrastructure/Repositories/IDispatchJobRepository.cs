using TransactionDispatch.Domain.Enums;
using TransactionDispatch.Infrastructure.Entities;

namespace TransactionDispatch.Infrastructure.Repositories;

public interface IDispatchJobRepository
{
    Task<DispatchJob> CreateAsync(DispatchJob entity, CancellationToken cancellationToken = default);
    Task<DispatchJob?> GetByIdAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IEnumerable<DispatchJob>> GetPendingJobsAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<DispatchJob>> GetRunningJobsAsync(CancellationToken cancellationToken = default);
    Task<DispatchJob?> GetRecentJobForFolderAsync(string folderPath, DateTime cutoffTime, CancellationToken cancellationToken = default);
    Task UpdateAsync(DispatchJob entity, CancellationToken cancellationToken = default);
    Task<bool> TryClaimJobAsync(Guid jobId, string claimedBy, CancellationToken cancellationToken = default);
    Task IncrementCountersAsync(Guid jobId, int processedDelta, int successfulDelta, int failedDelta, CancellationToken cancellationToken = default);
    Task SetTotalFilesAsync(Guid jobId, int totalFiles, CancellationToken cancellationToken = default);
    Task SetCompletionStateAsync(Guid jobId, DispatchJobState state, string? error, CancellationToken cancellationToken = default);
}

