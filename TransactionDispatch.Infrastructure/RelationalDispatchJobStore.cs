using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionDispatch.Application.Interfaces;
using TransactionDispatch.Application.Options;
using TransactionDispatch.Infrastructure.Entities;
using TransactionDispatch.Infrastructure.Repositories;
using DomainJob = TransactionDispatch.Domain.DispatchJob;
using TransactionDispatch.Domain.Enums;

namespace TransactionDispatch.Infrastructure;

public sealed class RelationalDispatchJobStore : IDispatchJobStore
{
    private readonly IDispatchJobRepository _jobRepository;
    private readonly IProcessedFileRepository _fileRepository;
    private readonly IdempotencyOptions _idempotencyOptions;
    private readonly ILogger<RelationalDispatchJobStore> _logger;

    public RelationalDispatchJobStore(
        IDispatchJobRepository jobRepository,
        IProcessedFileRepository fileRepository,
        IOptions<IdempotencyOptions> idempotencyOptions,
        ILogger<RelationalDispatchJobStore> logger)
    {
        _jobRepository = jobRepository;
        _fileRepository = fileRepository;
        _idempotencyOptions = idempotencyOptions.Value;
        _logger = logger;
    }

    public async Task<DomainJob> CreateAsync(string folderPath, bool deleteAfterSend, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("folderPath must not be empty or whitespace.", nameof(folderPath));

        if (_idempotencyOptions.EnableFolderIdempotency)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-_idempotencyOptions.FolderIdempotencyWindowMinutes);
            var existing = await _jobRepository.GetRecentJobForFolderAsync(folderPath, cutoff, cancellationToken);
            if (existing is not null)
            {
                _logger.LogWarning(
                    "Rejected duplicate dispatch for folder '{FolderPath}': job {ExistingJobId} is still within the idempotency window.",
                    folderPath, existing.JobId);
                throw new InvalidOperationException(
                    $"Folder '{folderPath}' was already dispatched recently. Existing job ID: {existing.JobId}");
            }
        }

        var entity = new DispatchJob
        {
            JobId = Guid.NewGuid(),
            FolderPath = folderPath,
            DeleteAfterSend = deleteAfterSend,
            CreatedAt = DateTime.UtcNow,
            State = DispatchJobState.Queued
        };

        await _jobRepository.CreateAsync(entity, cancellationToken);

        _logger.LogDebug("Dispatch job {JobId} persisted for folder {FolderPath}", entity.JobId, folderPath);

        return ToDomain(entity);
    }

    public async Task<DomainJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Job ID must not be empty.", nameof(jobId));

        var entity = await _jobRepository.GetByIdAsync(jobId, cancellationToken);
        return entity is not null ? ToDomain(entity) : null;
    }

    public async Task<IEnumerable<DomainJob>> GetPendingJobsAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _jobRepository.GetPendingJobsAsync(cancellationToken);
        return entities.Select(ToDomain);
    }

    public async Task<IEnumerable<DomainJob>> GetRunningJobsAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _jobRepository.GetRunningJobsAsync(cancellationToken);
        return entities.Select(ToDomain);
    }

    public Task MarkRunningAsync(Guid jobId, int totalFiles, CancellationToken cancellationToken = default)
        => _jobRepository.SetTotalFilesAsync(jobId, totalFiles, cancellationToken);

    public Task MarkFilesProcessedAsync(Guid jobId, int processedDelta, int successfulDelta, int failedDelta, CancellationToken cancellationToken = default)
        => _jobRepository.IncrementCountersAsync(jobId, processedDelta, successfulDelta, failedDelta, cancellationToken);

    public Task MarkFailedAsync(Guid jobId, string error, CancellationToken cancellationToken = default)
        => _jobRepository.SetCompletionStateAsync(jobId, DispatchJobState.Failed, error, cancellationToken);

    public Task MarkCompletedAsync(Guid jobId, CancellationToken cancellationToken = default)
        => _jobRepository.SetCompletionStateAsync(jobId, DispatchJobState.Completed, null, cancellationToken);

    public Task<bool> TryClaimJobAsync(Guid jobId, string claimedBy, CancellationToken cancellationToken = default)
        => _jobRepository.TryClaimJobAsync(jobId, claimedBy, cancellationToken);

    public Task MarkCancelledAsync(Guid jobId, CancellationToken cancellationToken = default)
        => _jobRepository.SetCompletionStateAsync(jobId, DispatchJobState.Cancelled, null, cancellationToken);

    public Task<bool> IsFileAlreadyProcessedAsync(Guid jobId, string filePath, CancellationToken cancellationToken = default)
        => _idempotencyOptions.EnableFileIdempotency
            ? _fileRepository.IsFileAlreadyProcessedAsync(jobId, filePath, cancellationToken)
            : Task.FromResult(false);

    public Task MarkFileAsProcessedAsync(Guid jobId, string filePath, bool success, CancellationToken cancellationToken = default)
        => _idempotencyOptions.EnableFileIdempotency
            ? _fileRepository.MarkFileAsProcessedAsync(jobId, filePath, success, cancellationToken)
            : Task.CompletedTask;

    private static DomainJob ToDomain(DispatchJob e) => new()
    {
        JobId = e.JobId,
        FolderPath = e.FolderPath,
        DeleteAfterSend = e.DeleteAfterSend,
        CreatedAt = new DateTimeOffset(e.CreatedAt, TimeSpan.Zero),
        StartedAt = e.StartedAt.HasValue ? new DateTimeOffset(e.StartedAt.Value, TimeSpan.Zero) : null,
        CompletedAt = e.CompletedAt.HasValue ? new DateTimeOffset(e.CompletedAt.Value, TimeSpan.Zero) : null,
        State = e.State,
        TotalFiles = e.TotalFiles,
        ProcessedFiles = e.ProcessedFiles,
        SuccessfulFiles = e.SuccessfulFiles,
        FailedFiles = e.FailedFiles,
        Error = e.Error,
        ClaimedBy = e.ClaimedBy
    };
}
