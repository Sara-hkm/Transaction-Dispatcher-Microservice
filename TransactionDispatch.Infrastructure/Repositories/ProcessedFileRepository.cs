using Microsoft.EntityFrameworkCore;
using Npgsql;
using TransactionDispatch.Infrastructure.Data;
using TransactionDispatch.Infrastructure.Entities;

namespace TransactionDispatch.Infrastructure.Repositories;

public class ProcessedFileRepository : IProcessedFileRepository
{
    private readonly IDbContextFactory<TransactionDispatchDbContext> _contextFactory;

    public ProcessedFileRepository(IDbContextFactory<TransactionDispatchDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<bool> IsFileAlreadyProcessedAsync(Guid jobId, string filePath, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ProcessedFiles
            .AnyAsync(pf => pf.JobId == jobId && pf.FilePath == filePath, cancellationToken);
    }

    public async Task MarkFileAsProcessedAsync(Guid jobId, string filePath, bool success, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var processedFile = new ProcessedFile
        {
            JobId = jobId,
            FilePath = filePath,
            ProcessedAt = DateTimeOffset.UtcNow,
            Success = success
        };

        context.ProcessedFiles.Add(processedFile);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Concurrent worker already recorded this file — safe to ignore.
        }
    }

    /// <summary>
    /// Returns true only for unique-constraint violations so that other persistence
    /// failures (e.g. FK violations, I/O errors) are not silently swallowed.
    /// </summary>
    internal static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // PostgreSQL: SQLSTATE 23505 = unique_violation
        if (ex.InnerException is PostgresException { SqlState: "23505" })
            return true;

        // SQLite (used in unit tests): checked by message to avoid a test-only
        // package dependency in the production assembly.
        return ex.InnerException?.GetType().Name == "SqliteException"
               && (ex.InnerException.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                   || ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase));
    }
}
