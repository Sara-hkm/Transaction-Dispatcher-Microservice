using Microsoft.AspNetCore.Mvc;
using TransactionDispatch.Application;
using TransactionDispatch.Application.Interfaces;

namespace TransactionDispatch.Api.Controllers;

[ApiController]
/// <summary>
/// Handles HTTP requests for dispatching  transaction files to Kafka
/// and querying the status of running or completed dispatch jobs.
/// </summary>
public sealed class DispatchController(IDispatchService dispatchService) : ControllerBase
{
    /// <summary>
    /// Submits a new dispatch job that publishes all supported files in the specified folder to Kafka.
    /// The job runs asynchronously in the background; use <see cref="GetStatus"/> to track progress.
    /// </summary>
    /// <param name="request">
    /// Request body containing:
    /// <list type="bullet">
    /// <item><term>folderPath</term><description>Absolute path to the folder containing XML transaction files. Must not be empty or whitespace.</description></item>
    /// <item><term>deleteAfterSend</term><description>If <c>true</c>, successfully dispatched files are deleted from disk. Defaults to <c>false</c>.</description></item>
    /// </list>
    /// </param>
    /// <param name="cancellationToken">Propagates client disconnection.</param>
    /// <returns>
    /// <list type="bullet">
    /// <item><term>202 Accepted</term><description><c>{ jobId: guid }</c> — job created successfully.</description></item>
    /// <item><term>400 Bad Request</term><description>Validation failure (empty/whitespace folderPath).</description></item>
    /// <item><term>409 Conflict</term><description>The same folder was already dispatched recently (folder-idempotency window active).</description></item>
    /// </list>
    /// </returns>
    [HttpPost("/dispatch-transactions")]
    public async Task<ActionResult<object>> DispatchTransactions([FromBody] DispatchRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(new ValidationProblemDetails(ModelState));

        try
        {
            var jobId = await dispatchService.DispatchTransactionsAsync(request, cancellationToken);
            return Accepted(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Returns the current status and progress of a dispatch job.
    /// </summary>
    /// <param name="jobId">The GUID returned by <c>POST /dispatch-transactions</c>. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="cancellationToken">Propagates client disconnection.</param>
    /// <returns>
    /// <list type="bullet">
    /// <item><term>200 OK</term><description>Body contains current or final progress details; inspect the <c>state</c> field to determine whether the job is still running.</description></item>
    /// <item><term>400 Bad Request</term><description><paramref name="jobId"/> is <see cref="Guid.Empty"/>.</description></item>
    /// <item><term>404 Not Found</term><description>No job with the given ID exists.</description></item>
    /// </list>
    /// The response body always includes: <c>progress</c>, <c>state</c>, <c>totalFiles</c>, <c>processed</c>, <c>successful</c>, <c>failed</c>, <c>error</c>.
    /// </returns>
    [HttpGet("/dispatch-status/{jobId:guid}")]
    public async Task<ActionResult<object>> GetStatus(Guid jobId, CancellationToken cancellationToken)
    {
        //check if guid is not empty
        if (jobId == Guid.Empty)
            return BadRequest(new { error = "Invalid job ID" });

        var job = await dispatchService.GetJobStatusAsync(jobId, cancellationToken);

        if (job is null)
            return NotFound();
        

        var body = new
        {
            progress = job.Progress,
            state = job.State.ToString(),
            totalFiles = job.TotalFiles,
            processed = job.ProcessedFiles,
            successful = job.SuccessfulFiles,
            failed = job.FailedFiles,
            error = job.Error
        };

        return Ok(body);
    }
}
