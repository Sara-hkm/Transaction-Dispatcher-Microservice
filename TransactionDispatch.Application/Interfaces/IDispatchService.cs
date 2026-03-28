using TransactionDispatch.Domain;

namespace TransactionDispatch.Application;

/// <summary>
/// Application-layer façade for submitting and querying transaction dispatch jobs.
/// Implementations coordinate with <see cref="Interfaces.IDispatchJobStore"/> to persist job records.
/// </summary>
public interface IDispatchService
{
    /// <summary>
    /// Creates a new dispatch job for the given folder and enqueues it for background processing.
    /// </summary>
    /// <param name="request">Dispatch parameters — folder path and optional delete-after-send flag.</param>
    /// <param name="cancellationToken">Propagates cancellation from the HTTP request.</param>
    /// <returns>The <see cref="Guid"/> of the newly created job.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><c>FolderPath</c> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Folder-idempotency: the same folder was dispatched recently.</exception>
    Task<Guid> DispatchTransactionsAsync(DispatchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves the current state and progress of the specified dispatch job.
    /// </summary>
    /// <param name="jobId">Unique identifier of the job to look up.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>The <see cref="DispatchJob"/> if found; <c>null</c> if no such job exists.</returns>
    /// <exception cref="ArgumentException"><paramref name="jobId"/> is <see cref="Guid.Empty"/>.</exception>
    Task<DispatchJob?> GetJobStatusAsync(Guid jobId, CancellationToken cancellationToken = default);
}
