using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TransactionDispatch.Application.Options;
using TransactionDispatch.Domain.Enums;
using TransactionDispatch.Infrastructure.Data;
using TransactionDispatch.Infrastructure.Entities;
using TransactionDispatch.Infrastructure.Repositories;

namespace TransactionDispatch.Tests;

/// <summary>Minimal IDbContextFactory shim backed by a single SQLite in-memory connection.</summary>
internal sealed class SqliteContextFactory : IDbContextFactory<TransactionDispatchDbContext>
{
    private readonly DbContextOptions<TransactionDispatchDbContext> _options;

    public SqliteContextFactory(DbContextOptions<TransactionDispatchDbContext> options)
        => _options = options;

    public TransactionDispatchDbContext CreateDbContext() => new(_options);
}

public sealed class DispatchJobRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DispatchJobRepository _repository;
    private readonly ProcessedFileRepository _processedFileRepository;

    public DispatchJobRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TransactionDispatchDbContext>()
            .UseSqlite(_connection)
            .Options;

        // Create schema once via a throw-away context
        using var seed = new TransactionDispatchDbContext(options);
        seed.Database.EnsureCreated();

        var factory = new SqliteContextFactory(options);
        _repository = new DispatchJobRepository(factory, Options.Create(new DispatchOptions()));
        _processedFileRepository = new ProcessedFileRepository(factory);
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private static DispatchJob MakeJob(string folderPath = "c:/test", DispatchJobState state = DispatchJobState.Queued) =>
        new()
        {
            JobId = Guid.NewGuid(),
            FolderPath = folderPath,
            DeleteAfterSend = false,
            CreatedAt = DateTime.UtcNow,
            State = state,
            TotalFiles = 0,
            ProcessedFiles = 0,
            SuccessfulFiles = 0,
            FailedFiles = 0
        };

    // ---- DispatchJobRepository ----

    [Fact]
    public async Task CreateAsync_Persists_And_Returns_Entity()
    {
        var entity = MakeJob();

        var result = await _repository.CreateAsync(entity);

        Assert.Equal(entity.JobId, result.JobId);
        Assert.Equal(entity.FolderPath, result.FolderPath);
    }

    [Fact]
    public async Task GetByIdAsync_Returns_Entity_When_Exists()
    {
        var entity = MakeJob();
        await _repository.CreateAsync(entity);

        var result = await _repository.GetByIdAsync(entity.JobId);

        Assert.NotNull(result);
        Assert.Equal(entity.JobId, result!.JobId);
    }

    [Fact]
    public async Task GetByIdAsync_Returns_Null_When_Not_Found()
    {
        var result = await _repository.GetByIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetRunningJobsAsync_Returns_Only_Running()
    {
        var running = MakeJob(state: DispatchJobState.Running);
        var completed = MakeJob(state: DispatchJobState.Completed);
        await _repository.CreateAsync(running);
        await _repository.CreateAsync(completed);

        var result = (await _repository.GetRunningJobsAsync()).ToList();

        Assert.Single(result);
        Assert.Equal(running.JobId, result[0].JobId);
    }

    [Fact]
    public async Task GetRecentJobForFolderAsync_Returns_Recent_Non_Failed_Job()
    {
        var recent = MakeJob("c:/folder");
        recent.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
        await _repository.CreateAsync(recent);

        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        var result = await _repository.GetRecentJobForFolderAsync("c:/folder", cutoff);

        Assert.NotNull(result);
        Assert.Equal(recent.JobId, result!.JobId);
    }

    [Fact]
    public async Task GetRecentJobForFolderAsync_Excludes_Failed_Jobs()
    {
        var failedJob = MakeJob("c:/folder2", DispatchJobState.Failed);
        failedJob.CreatedAt = DateTime.UtcNow.AddMinutes(-5);
        await _repository.CreateAsync(failedJob);

        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        var result = await _repository.GetRecentJobForFolderAsync("c:/folder2", cutoff);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetRecentJobForFolderAsync_Excludes_Old_Jobs()
    {
        var oldJob = MakeJob("c:/folder3");
        oldJob.CreatedAt = DateTime.UtcNow.AddHours(-2);
        await _repository.CreateAsync(oldJob);

        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        var result = await _repository.GetRecentJobForFolderAsync("c:/folder3", cutoff);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_Persists_Changes()
    {
        var entity = MakeJob();
        await _repository.CreateAsync(entity);

        entity.State = DispatchJobState.Running;
        entity.StartedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(entity);

        var updated = await _repository.GetByIdAsync(entity.JobId);
        Assert.Equal(DispatchJobState.Running, updated!.State);
        Assert.NotNull(updated.StartedAt);
    }

    // ---- ProcessedFileRepository ----

    [Fact]
    public async Task IsFileAlreadyProcessedAsync_Returns_False_When_Not_Processed()
    {
        var job = MakeJob();
        await _repository.CreateAsync(job);

        var result = await _processedFileRepository.IsFileAlreadyProcessedAsync(job.JobId, "file.xml");

        Assert.False(result);
    }

    [Fact]
    public async Task IsFileAlreadyProcessedAsync_Returns_True_After_Mark()
    {
        var job = MakeJob();
        await _repository.CreateAsync(job);

        await _processedFileRepository.MarkFileAsProcessedAsync(job.JobId, "file.xml", true);
        var result = await _processedFileRepository.IsFileAlreadyProcessedAsync(job.JobId, "file.xml");

        Assert.True(result);
    }

    [Fact]
    public async Task MarkFileAsProcessedAsync_Ignores_Duplicate()
    {
        var job = MakeJob();
        await _repository.CreateAsync(job);

        await _processedFileRepository.MarkFileAsProcessedAsync(job.JobId, "dup.xml", true);

        // Second call for the same file should not throw
        var ex = await Record.ExceptionAsync(
            () => _processedFileRepository.MarkFileAsProcessedAsync(job.JobId, "dup.xml", false));
        Assert.Null(ex);
    }

    // ---- New repository method tests ----

    [Fact]
    public async Task GetPendingJobsAsync_Returns_Only_Queued_In_FIFO_Order()
    {
        var older = MakeJob();
        older.CreatedAt = DateTime.UtcNow.AddMinutes(-5);
        var newer = MakeJob();
        newer.CreatedAt = DateTime.UtcNow;
        var running = MakeJob(state: DispatchJobState.Running);
        await _repository.CreateAsync(older);
        await _repository.CreateAsync(newer);
        await _repository.CreateAsync(running);

        var result = (await _repository.GetPendingJobsAsync()).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(older.JobId, result[0].JobId);   // FIFO: oldest first
        Assert.Equal(newer.JobId, result[1].JobId);
    }

    [Fact]
    public async Task SetTotalFilesAsync_Persists_TotalFiles()
    {
        var entity = MakeJob();
        await _repository.CreateAsync(entity);

        await _repository.SetTotalFilesAsync(entity.JobId, 42);

        var result = await _repository.GetByIdAsync(entity.JobId);
        Assert.Equal(42, result!.TotalFiles);
    }

    [Fact]
    public async Task IncrementCountersAsync_Adds_Deltas_To_Existing_Values()
    {
        var entity = MakeJob();
        await _repository.CreateAsync(entity);

        await _repository.IncrementCountersAsync(entity.JobId, 10, 8, 2);

        var result = await _repository.GetByIdAsync(entity.JobId);
        Assert.Equal(10, result!.ProcessedFiles);
        Assert.Equal(8, result.SuccessfulFiles);
        Assert.Equal(2, result.FailedFiles);
    }

    [Fact]
    public async Task SetCompletionStateAsync_Sets_State_CompletedAt_And_Error()
    {
        var entity = MakeJob();
        await _repository.CreateAsync(entity);

        await _repository.SetCompletionStateAsync(entity.JobId, DispatchJobState.Failed, "disk full");

        var result = await _repository.GetByIdAsync(entity.JobId);
        Assert.Equal(DispatchJobState.Failed, result!.State);
        Assert.NotNull(result.CompletedAt);
        Assert.Equal("disk full", result.Error);
    }

    [Fact]
    public async Task SetCompletionStateAsync_Completed_Clears_Error()
    {
        var entity = MakeJob();
        await _repository.CreateAsync(entity);

        await _repository.SetCompletionStateAsync(entity.JobId, DispatchJobState.Completed, null);

        var result = await _repository.GetByIdAsync(entity.JobId);
        Assert.Equal(DispatchJobState.Completed, result!.State);
        Assert.Null(result.Error);
    }

    // ---- Guard / validation tests ----

    [Fact]
    public async Task CreateAsync_NullEntity_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _repository.CreateAsync(null!));
    }

    [Fact]
    public async Task CreateAsync_WhitespaceFolderPath_ThrowsArgumentException()
    {
        var entity = MakeJob("   ");
        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.CreateAsync(entity));
    }

    [Fact]
    public async Task GetByIdAsync_EmptyJobId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.GetByIdAsync(Guid.Empty));
    }

    [Fact]
    public async Task TryClaimJobAsync_EmptyJobId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.TryClaimJobAsync(Guid.Empty, "worker-1"));
    }

    [Fact]
    public async Task TryClaimJobAsync_WhitespaceClaimedBy_ThrowsArgumentException()
    {
        var job = MakeJob();
        await _repository.CreateAsync(job);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.TryClaimJobAsync(job.JobId, "   "));
    }

    [Fact]
    public async Task TryClaimJobAsync_QueuedJob_ReturnsTrueAndSetsRunning()
    {
        var job = MakeJob(state: DispatchJobState.Queued);
        await _repository.CreateAsync(job);

        var claimed = await _repository.TryClaimJobAsync(job.JobId, "worker-1");

        Assert.True(claimed);
        var updated = await _repository.GetByIdAsync(job.JobId);
        Assert.Equal(DispatchJobState.Running, updated!.State);
        Assert.Equal("worker-1", updated.ClaimedBy);
    }

    [Fact]
    public async Task TryClaimJobAsync_AlreadyRunningJob_ReturnsFalse()
    {
        var job = MakeJob(state: DispatchJobState.Running);
        await _repository.CreateAsync(job);

        var claimed = await _repository.TryClaimJobAsync(job.JobId, "worker-2");

        Assert.False(claimed);
    }

    [Fact]
    public async Task TryClaimJobAsync_NonExistentJobId_ReturnsFalse()
    {
        var claimed = await _repository.TryClaimJobAsync(Guid.NewGuid(), "worker-1");
        Assert.False(claimed);
    }

    [Fact]
    public void ProcessedFile_JobNavigationProperty_CanBeSetAndRead()
    {
        var dispatchJob = new TransactionDispatch.Infrastructure.Entities.DispatchJob
        {
            JobId = Guid.NewGuid(),
            FolderPath = "/test",
            DeleteAfterSend = false,
            CreatedAt = DateTime.UtcNow,
            State = TransactionDispatch.Domain.Enums.DispatchJobState.Queued
        };
        var processedFile = new TransactionDispatch.Infrastructure.Entities.ProcessedFile
        {
            JobId = dispatchJob.JobId,
            FilePath = "file.xml"
        };

        processedFile.Job = dispatchJob;

        Assert.Same(dispatchJob, processedFile.Job);
    }

    // ---- ProcessedFileRepository.IsUniqueConstraintViolation branch coverage ----

    [Fact]
    public void IsUniqueConstraintViolation_WithNonSqliteNonPostgresInner_ReturnsFalse()
    {
        var inner = new InvalidOperationException("some other error");
        var dbEx = new Microsoft.EntityFrameworkCore.DbUpdateException("fail", inner);

        var result = ProcessedFileRepository.IsUniqueConstraintViolation(dbEx);

        Assert.False(result);
    }

    [Fact]
    public void IsUniqueConstraintViolation_WithNullInner_ReturnsFalse()
    {
        var dbEx = new Microsoft.EntityFrameworkCore.DbUpdateException("fail", (Exception?)null);

        var result = ProcessedFileRepository.IsUniqueConstraintViolation(dbEx);

        Assert.False(result);
    }
}


