using TransactionDispatch.Domain;

namespace TransactionDispatch.Application.Interfaces;

/// <summary>
/// Persistence abstraction for dispatch job lifecycle management.
/// All mutations are idempotent where noted; implementations must be safe for concurrent callers.
/// </summary>
public interface IDispatchJobStore
{
    /// <summary>Creates a new <c>Queued</c> job for the given folder and returns the persisted domain object.</summary>
    /// <param name="folderPath">Absolute path to the source folder. Must not be empty or whitespace.</param>
    /// <param name="deleteAfterSend">Whether files should be deleted from disk after successful dispatch.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <exception cref="InvalidOperationException">Thrown when folder-idempotency rejects a duplicate submission.</exception>
    Task<DispatchJob> CreateAsync(string folderPath, bool deleteAfterSend, CancellationToken cancellationToken = default);

    /// <summary>Returns the job with the given ID, or <c>null</c> if it does not exist.</summary>
    Task<DispatchJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Returns all jobs currently in the <c>Queued</c> state, ordered oldest-first up to <c>MaxPollBatchSize</c>.</summary>
    Task<IEnumerable<DispatchJob>> GetPendingJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns all jobs currently in the <c>Running</c> state (used for crash-recovery tooling).</summary>
    Task<IEnumerable<DispatchJob>> GetRunningJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions the job to <c>Running</c> and records the total number of files that will be processed.
    /// </summary>
    /// <param name="jobId">Target job.</param>
    /// <param name="totalFiles">Total matchable file count discovered in the folder.</param>
    Task MarkRunningAsync(Guid jobId, int totalFiles, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically adds deltas to the job's progress counters.
    /// Called in batches (every <c>ProgressSaveEvery</c> files) to reduce DB write pressure.
    /// </summary>
    /// <param name="processedDelta">Number of files processed since the last flush.</param>
    /// <param name="successfulDelta">Of those, how many succeeded.</param>
    /// <param name="failedDelta">Of those, how many failed.</param>
    Task MarkFilesProcessedAsync(Guid jobId, int processedDelta, int successfulDelta, int failedDelta, CancellationToken cancellationToken = default);

    /// <summary>Marks the job as <c>Failed</c> and records the error message.</summary>
    Task MarkFailedAsync(Guid jobId, string error, CancellationToken cancellationToken = default);

    /// <summary>Marks the job as <c>Completed</c> and records the completion timestamp.</summary>
    Task MarkCompletedAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to atomically claim the job for the specified worker.
    /// Only succeeds if the job is still in <c>Queued</c> state, preventing double-processing across instances.
    /// </summary>
    /// <param name="claimedBy">Unique worker identifier (e.g. <c>hostname:pid</c>).</param>
    /// <returns><c>true</c> if the claim succeeded; <c>false</c> if another worker already owns the job.</returns>
    Task<bool> TryClaimJobAsync(Guid jobId, string claimedBy, CancellationToken cancellationToken = default);

    /// <summary>Marks the job as <c>Cancelled</c>. Called when the job's cancellation token is triggered.</summary>
    Task MarkCancelledAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Returns <c>true</c> if the specified file was already successfully processed in this job (file-idempotency check).</summary>
    Task<bool> IsFileAlreadyProcessedAsync(Guid jobId, string filePath, CancellationToken cancellationToken = default);

    /// <summary>Records that the specified file has been processed, storing whether it succeeded.</summary>
    Task MarkFileAsProcessedAsync(Guid jobId, string filePath, bool success, CancellationToken cancellationToken = default);
}
