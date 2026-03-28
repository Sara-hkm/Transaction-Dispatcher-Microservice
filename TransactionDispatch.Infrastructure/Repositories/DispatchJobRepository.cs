using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TransactionDispatch.Application.Options;
using TransactionDispatch.Domain.Enums;
using TransactionDispatch.Infrastructure.Data;
using TransactionDispatch.Infrastructure.Entities;

namespace TransactionDispatch.Infrastructure.Repositories;

public class DispatchJobRepository : IDispatchJobRepository
{
    private readonly IDbContextFactory<TransactionDispatchDbContext> _contextFactory;
    private readonly IOptions<DispatchOptions> _dispatchOptions;

    public DispatchJobRepository(
        IDbContextFactory<TransactionDispatchDbContext> contextFactory,
        IOptions<DispatchOptions> dispatchOptions)
    {
        _contextFactory = contextFactory;
        _dispatchOptions = dispatchOptions;
    }

    public async Task<DispatchJob> CreateAsync(DispatchJob entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (string.IsNullOrWhiteSpace(entity.FolderPath))
            throw new ArgumentException("FolderPath must not be empty or whitespace.", nameof(entity));

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.DispatchJobs.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<DispatchJob?> GetByIdAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Job ID must not be empty.", nameof(jobId));

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.DispatchJobs
            .FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken);
    }

    public async Task<IEnumerable<DispatchJob>> GetPendingJobsAsync(CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Max(1, _dispatchOptions.Value.MaxPollBatchSize);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.DispatchJobs
            .Where(j => j.State == DispatchJobState.Queued)
            .OrderBy(j => j.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> TryClaimJobAsync(Guid jobId, string claimedBy, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Job ID must not be empty.", nameof(jobId));
        if (string.IsNullOrWhiteSpace(claimedBy))
            throw new ArgumentException("claimedBy must not be empty or whitespace.", nameof(claimedBy));

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var rowsAffected = await context.DispatchJobs
            .Where(j => j.JobId == jobId && j.State == DispatchJobState.Queued)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.State, DispatchJobState.Running)
                       .SetProperty(j => j.ClaimedBy, claimedBy)
                       .SetProperty(j => j.StartedAt, DateTime.UtcNow),
                cancellationToken);
        return rowsAffected > 0;
    }

    public async Task<IEnumerable<DispatchJob>> GetRunningJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.DispatchJobs
            .Where(j => j.State == DispatchJobState.Running)
            .ToListAsync(cancellationToken);
    }

    public async Task<DispatchJob?> GetRecentJobForFolderAsync(string folderPath, DateTime cutoffTime, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.DispatchJobs
            .Where(j => j.FolderPath == folderPath
                     && j.CreatedAt > cutoffTime
                     && j.State != DispatchJobState.Failed
                     && j.State != DispatchJobState.Cancelled)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task UpdateAsync(DispatchJob entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.DispatchJobs.Update(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task IncrementCountersAsync(Guid jobId, int processedDelta, int successfulDelta, int failedDelta, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.DispatchJobs
            .Where(j => j.JobId == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.ProcessedFiles, j => j.ProcessedFiles + processedDelta)
                .SetProperty(j => j.SuccessfulFiles, j => j.SuccessfulFiles + successfulDelta)
                .SetProperty(j => j.FailedFiles, j => j.FailedFiles + failedDelta),
                cancellationToken);
    }

    public async Task SetTotalFilesAsync(Guid jobId, int totalFiles, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.DispatchJobs
            .Where(j => j.JobId == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.TotalFiles, totalFiles), cancellationToken);
    }

    public async Task SetCompletionStateAsync(Guid jobId, DispatchJobState state, string? error, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.DispatchJobs
            .Where(j => j.JobId == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, state)
                .SetProperty(j => j.CompletedAt, DateTime.UtcNow)
                .SetProperty(j => j.Error, error),
                cancellationToken);
    }
}
