using TransactionDispatch.Application.Interfaces;
using TransactionDispatch.Application;
using TransactionDispatch.Domain;
using Microsoft.Extensions.Logging;

namespace TransactionDispatch.Infrastructure;

/// <summary>
/// Default implementation of <see cref="IDispatchService"/>.
/// Validates the incoming request and delegates persistence to <see cref="IDispatchJobStore"/>.
/// </summary>
public sealed class DispatchService(
    IDispatchJobStore jobStore,
    ILogger<DispatchService> logger) : IDispatchService
{
    /// <summary>
    /// Validates <paramref name="request"/>, creates a <c>Queued</c> job record in the store,
    /// and returns its ID. The background service picks it up on the next poll cycle.
    /// </summary>
    /// <param name="request">Dispatch parameters. <c>FolderPath</c> must not be null, empty, or whitespace.</param>
    /// <param name="cancellationToken">Propagates cancellation from the HTTP request.</param>
    /// <returns>The new job ID.</returns>
    /// <exception cref="ArgumentException"><c>FolderPath</c> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Folder-idempotency window is active for the given folder.</exception>
    public async Task<Guid> DispatchTransactionsAsync(DispatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FolderPath))
            throw new ArgumentException("FolderPath must not be empty or whitespace.", nameof(request));

        logger.LogDebug("Creating dispatch job for folder {FolderPath} (deleteAfterSend={DeleteAfterSend})",
            request.FolderPath, request.DeleteAfterSend);

        var job = await jobStore.CreateAsync(request.FolderPath, request.DeleteAfterSend, cancellationToken);

        logger.LogInformation("Dispatch job {JobId} created for folder {FolderPath}", job.JobId, request.FolderPath);
        return job.JobId;
    }

    /// <summary>
    /// Retrieves the current state and progress of a job by its ID.
    /// Returns <c>null</c> if no job with the given ID exists.
    /// </summary>
    /// <param name="jobId">The job identifier. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>The <see cref="DispatchJob"/> domain object, or <c>null</c> if not found.</returns>
    /// <exception cref="ArgumentException"><paramref name="jobId"/> is <see cref="Guid.Empty"/>.</exception>
    public Task<DispatchJob?> GetJobStatusAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Job ID must not be empty.", nameof(jobId));

        return jobStore.GetJobAsync(jobId, cancellationToken);
    }
}
