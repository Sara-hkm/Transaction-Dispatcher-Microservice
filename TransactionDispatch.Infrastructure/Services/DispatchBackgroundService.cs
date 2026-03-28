using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionDispatch.Application;
using TransactionDispatch.Application.Interfaces;
using TransactionDispatch.Application.Options;

namespace TransactionDispatch.Infrastructure.Services;

/// <summary>
/// Long-running hosted service that polls the job store for <c>Queued</c> jobs and processes them.
/// Multiple instances can run concurrently; optimistic claiming (<see cref="IDispatchJobStore.TryClaimJobAsync"/>)
/// prevents double-processing across hosts.
/// </summary>
public sealed class DispatchBackgroundService(
    IServiceScopeFactory scopeFactory,
    IJobCancellationRegistry cancellationRegistry,
    ITransactionDispatcher dispatcher,
    IOptions<DispatchOptions> dispatchOptions,
    ILogger<DispatchBackgroundService> logger) : BackgroundService
{
    private TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(1, dispatchOptions.Value.PollIntervalSeconds));
    private static readonly string WorkerId =
        $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>
    /// Main polling loop. On each cycle:
    /// <list type="number">
    /// <item>Fetches a batch of <c>Queued</c> jobs from the store.</item>
    /// <item>Fans out processing across up to <c>MaxParallelism</c> concurrent tasks.</item>
    /// <item>Each job is claimed atomically before processing begins.</item>
    /// <item>If the batch is empty, waits <c>PollIntervalSeconds</c> before retrying.</item>
    /// <item>Unexpected exceptions are logged and the loop continues after one interval.</item>
    /// </list>
    /// </summary>
    /// <param name="stoppingToken">Cancelled by the host on shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Create a short-lived cope just to poll for pending jobs.
                List<Domain.DispatchJob> jobs;
                await using (var pollScope = scopeFactory.CreateAsyncScope())
                {
                    var pollStore = pollScope.ServiceProvider.GetRequiredService<IDispatchJobStore>();
                    var pendingJobs = await pollStore.GetPendingJobsAsync(stoppingToken);
                    jobs = (pendingJobs ?? []).ToList();
                }

                if (jobs.Count == 0)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                await Parallel.ForEachAsync(
                    jobs,
                    new ParallelOptions
                    {
                        CancellationToken = stoppingToken,
                        MaxDegreeOfParallelism = Math.Max(1, dispatchOptions.Value.MaxParallelism)
                    },
                    async (job, ct) =>
                    {
                        // Each job gets its own scope so DB operations are fully isolated.
                        await using var jobScope = scopeFactory.CreateAsyncScope();
                        var jobStore = jobScope.ServiceProvider.GetRequiredService<IDispatchJobStore>();

                        var claimed = await jobStore.TryClaimJobAsync(job.JobId, WorkerId, ct);
                        if (!claimed)
                        {
                            logger.LogDebug("Job {JobId} already claimed by another instance, skipping", job.JobId);
                            return;
                        }
                        await ProcessJobAsync(
                            new DispatchRequest(job.FolderPath, job.DeleteAfterSend),
                            job.JobId,
                            jobStore,
                            ct);
                    });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Poll cycle failed unexpectedly; retrying after {Interval}", PollInterval);
                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Processes a single claimed job end-to-end:
    /// <list type="number">
    /// <item>Verifies the source folder exists.</item>
    /// <item>Counts supported files and marks the job <c>Running</c>.</item>
    /// <item>Dispatches each file to Kafka in parallel with retry/backoff.</item>
    /// <item>Skips files already recorded as processed (file-idempotency).</item>
    /// <item>Flushes progress counters to the DB every <c>ProgressSaveEvery</c> files.</item>
    /// <item>Marks the job <c>Completed</c>, <c>Cancelled</c>, or <c>Failed</c> on exit.</item>
    /// </list>
    /// </summary>
    /// <param name="request">Folder path and delete-after-send flag for this job.</param>
    /// <param name="jobId">The job being processed.</param>
    /// <param name="jobStore">Scoped store instance for this job's DB operations.</param>
    /// <param name="cancellationToken">Combined host + job cancellation token.</param>
    private async Task ProcessJobAsync(DispatchRequest request, Guid jobId, IDispatchJobStore jobStore, CancellationToken cancellationToken)
    {
        var jobToken = cancellationRegistry.RegisterOrGet(jobId, cancellationToken);
        var progressLock = new object();
        var pendingProcessed = 0;
        var pendingSuccessful = 0;
        var pendingFailed = 0;

        async Task FlushPendingAsync()
        {
            int processed, successful, failed;
            lock (progressLock)
            {
                if (pendingProcessed == 0) return;
                (processed, successful, failed) = (pendingProcessed, pendingSuccessful, pendingFailed);
                pendingProcessed = pendingSuccessful = pendingFailed = 0;
            }
            await jobStore.MarkFilesProcessedAsync(jobId, processed, successful, failed);
        }

        try
        {
            if (!Directory.Exists(request.FolderPath))
            {
                await jobStore.MarkFailedAsync(jobId, $"Folder does not exist: {request.FolderPath}");
                return;
            }

            var options = dispatchOptions.Value;
            var normalized = options.SupportedExtensions
                .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : $".{e.ToLowerInvariant()}")
                .ToHashSet();

            // Count pass — lazy, never materializes all paths into memory.
            var totalFiles = Directory.EnumerateFiles(request.FolderPath)
                .Count(path => normalized.Contains(Path.GetExtension(path).ToLowerInvariant()));

            await jobStore.MarkRunningAsync(jobId, totalFiles);
            var flushEvery = Math.Max(1, options.ProgressSaveEvery);

            // Processing pass — a fresh enumeration so no paths are held in memory between the two scans.
            await Parallel.ForEachAsync(
                Directory.EnumerateFiles(request.FolderPath)
                    .Where(path => normalized.Contains(Path.GetExtension(path).ToLowerInvariant())),
                new ParallelOptions
                {
                    CancellationToken = jobToken,
                    MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism)
                },
                async (filePath, ct) =>
                {
                    ct.ThrowIfCancellationRequested();

                    bool ok;
                    if (await jobStore.IsFileAlreadyProcessedAsync(jobId, filePath, ct))
                    {
                        logger.LogDebug("File {FilePath} already processed in job {JobId}, skipping", filePath, jobId);
                        ok = true;
                    }
                    else
                    {
                        ok = await DispatchWithRetryAsync(filePath, options, ct);
                        if (ok)
                        {
                            await jobStore.MarkFileAsProcessedAsync(jobId, filePath, true, ct);
                            if (request.DeleteAfterSend)
                                TryDeleteFile(filePath);
                        }
                    }

                    bool shouldFlush;
                    int processed, successful, failed;
                    lock (progressLock)
                    {
                        pendingProcessed++;
                        if (ok) pendingSuccessful++;
                        else pendingFailed++;

                        shouldFlush = pendingProcessed >= flushEvery;
                        if (shouldFlush)
                        {
                            (processed, successful, failed) = (pendingProcessed, pendingSuccessful, pendingFailed);
                            pendingProcessed = pendingSuccessful = pendingFailed = 0;
                        }
                        else
                        {
                            (processed, successful, failed) = (0, 0, 0);
                        }
                    }

                    if (shouldFlush)
                        await jobStore.MarkFilesProcessedAsync(jobId, processed, successful, failed, ct);
                });

            await FlushPendingAsync();
            await jobStore.MarkCompletedAsync(jobId);
        }
        catch (OperationCanceledException)
        {
            await FlushPendingAsync();
            logger.LogInformation("Dispatch job {JobId} cancelled", jobId);
            await jobStore.MarkCancelledAsync(jobId);
        }
        catch (Exception ex)
        {
            await FlushPendingAsync();
            logger.LogError(ex, "Dispatch job {JobId} failed", jobId);
            await jobStore.MarkFailedAsync(jobId, ex.Message);
        }
        finally
        {
            cancellationRegistry.Complete(jobId);
        }
    }

    /// <summary>
    /// Attempts to dispatch a single file, retrying on transient exceptions up to <c>RetryCount</c> times
    /// with exponential backoff (<c>RetryDelayMilliseconds * attempt</c>).
    /// Returns <c>false</c> immediately (without retrying) if the dispatcher signals a permanent failure
    /// (e.g. file exceeds <c>MaxMessageSizeBytes</c>).
    /// </summary>
    /// <param name="filePath">Full path to the file to dispatch.</param>
    /// <param name="options">Current dispatch configuration.</param>
    /// <param name="cancellationToken">Cancels any in-flight delay or dispatch call.</param>
    /// <returns><c>true</c> if the file was dispatched successfully; <c>false</c> otherwise.</returns>
    private async Task<bool> DispatchWithRetryAsync(string filePath, DispatchOptions options, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= Math.Max(1, options.RetryCount); attempt++)
        {
            try
            {
                if (await dispatcher.DispatchAsync(filePath, cancellationToken))
                    return true;

                // Dispatcher returned false — permanent failure, do not retry.
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Attempt {Attempt} failed for file {FilePath}", attempt, filePath);
            }

            if (attempt < options.RetryCount)
                await Task.Delay(Math.Max(1, options.RetryDelayMilliseconds), cancellationToken);
        }

        logger.LogError("All retries failed for file {FilePath}", filePath);
        return false;
    }

    private void TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete file {FilePath}", filePath);
        }
    }
}
