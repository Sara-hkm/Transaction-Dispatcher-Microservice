using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TransactionDispatch.Application.Interfaces;
using TransactionDispatch.Application.Options;
using TransactionDispatch.Domain.Enums;
using TransactionDispatch.Infrastructure;
using TransactionDispatch.Infrastructure.Entities;
using TransactionDispatch.Infrastructure.Repositories;

namespace TransactionDispatch.Tests;

public sealed class RelationalDispatchJobStoreTests
{
    private readonly Mock<IDispatchJobRepository> _jobRepo = new();
    private readonly Mock<IProcessedFileRepository> _fileRepo = new();

    private RelationalDispatchJobStore CreateStore(IdempotencyOptions? opts = null) =>
        new(_jobRepo.Object, _fileRepo.Object,
            Options.Create(opts ?? new IdempotencyOptions
            {
                EnableFileIdempotency = true,
                EnableFolderIdempotency = true,
                FolderIdempotencyWindowMinutes = 60
            }),
            NullLogger<RelationalDispatchJobStore>.Instance);

    private static DispatchJob MakeEntity(string folderPath = "/test", DispatchJobState state = DispatchJobState.Queued) =>
        new()
        {
            JobId = Guid.NewGuid(),
            FolderPath = folderPath,
            DeleteAfterSend = false,
            CreatedAt = DateTime.UtcNow,
            State = state
        };

    // ── CreateAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ValidPath_CreatesAndReturnsDomainJob()
    {
        _jobRepo.Setup(r => r.GetRecentJobForFolderAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DispatchJob?)null);
        _jobRepo.Setup(r => r.CreateAsync(It.IsAny<DispatchJob>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DispatchJob e, CancellationToken _) => e);

        var result = await CreateStore().CreateAsync("/valid/path", false);

        Assert.NotEqual(Guid.Empty, result.JobId);
        Assert.Equal("/valid/path", result.FolderPath);
        Assert.Equal(DispatchJobState.Queued, result.State);
    }

    [Fact]
    public async Task CreateAsync_WhitespacePath_ThrowsArgumentException()
        => await Assert.ThrowsAsync<ArgumentException>(() => CreateStore().CreateAsync("   ", false));

    [Fact]
    public async Task CreateAsync_FolderIdempotencyEnabled_ExistingJob_ThrowsInvalidOperation()
    {
        _jobRepo.Setup(r => r.GetRecentJobForFolderAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeEntity());

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateStore().CreateAsync("/test", false));
    }

    [Fact]
    public async Task CreateAsync_FolderIdempotencyDisabled_AllowsDuplicate()
    {
        _jobRepo.Setup(r => r.CreateAsync(It.IsAny<DispatchJob>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DispatchJob e, CancellationToken _) => e);

        var result = await CreateStore(new IdempotencyOptions
        {
            EnableFolderIdempotency = false,
            EnableFileIdempotency = false
        }).CreateAsync("/test", false);

        Assert.NotEqual(Guid.Empty, result.JobId);
    }

    // ── GetJobAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetJobAsync_EmptyJobId_ThrowsArgumentException()
        => await Assert.ThrowsAsync<ArgumentException>(() => CreateStore().GetJobAsync(Guid.Empty));

    [Fact]
    public async Task GetJobAsync_ValidId_ReturnsJob()
    {
        var entity = MakeEntity();
        _jobRepo.Setup(r => r.GetByIdAsync(entity.JobId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(entity);

        var result = await CreateStore().GetJobAsync(entity.JobId);

        Assert.NotNull(result);
        Assert.Equal(entity.JobId, result.JobId);
        Assert.Equal(entity.FolderPath, result.FolderPath);
    }

    [Fact]
    public async Task GetJobAsync_NotFound_ReturnsNull()
    {
        _jobRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DispatchJob?)null);

        var result = await CreateStore().GetJobAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetJobAsync_ToDomain_MapsOptionalDates()
    {
        var now = DateTime.UtcNow;
        var entity = new DispatchJob
        {
            JobId = Guid.NewGuid(),
            FolderPath = "/test",
            DeleteAfterSend = true,
            CreatedAt = now,
            StartedAt = now,
            CompletedAt = now,
            State = DispatchJobState.Completed,
            TotalFiles = 5,
            ProcessedFiles = 5,
            SuccessfulFiles = 4,
            FailedFiles = 1,
            Error = "partial",
            ClaimedBy = "worker-1"
        };
        _jobRepo.Setup(r => r.GetByIdAsync(entity.JobId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(entity);

        var result = await CreateStore().GetJobAsync(entity.JobId);

        Assert.NotNull(result);
        Assert.True(result.StartedAt.HasValue);
        Assert.True(result.CompletedAt.HasValue);
        Assert.True(result.DeleteAfterSend);
        Assert.Equal("partial", result.Error);
        Assert.Equal("worker-1", result.ClaimedBy);
        Assert.Equal(5, result.TotalFiles);
        Assert.Equal(4, result.SuccessfulFiles);
        Assert.Equal(1, result.FailedFiles);
    }

    // ── GetPendingJobsAsync / GetRunningJobsAsync ──────────────────────────────

    [Fact]
    public async Task GetPendingJobsAsync_ReturnsMappedJobs()
    {
        _jobRepo.Setup(r => r.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { MakeEntity(), MakeEntity() });

        var result = (await CreateStore().GetPendingJobsAsync()).ToList();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetRunningJobsAsync_ReturnsMappedJobs()
    {
        _jobRepo.Setup(r => r.GetRunningJobsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { MakeEntity(state: DispatchJobState.Running) });

        var result = (await CreateStore().GetRunningJobsAsync()).ToList();

        Assert.Single(result);
    }

    // ── File idempotency ───────────────────────────────────────────────────────

    [Fact]
    public async Task IsFileAlreadyProcessedAsync_WhenEnabled_DelegatesToRepository()
    {
        var jobId = Guid.NewGuid();
        _fileRepo.Setup(r => r.IsFileAlreadyProcessedAsync(jobId, "file.xml", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(true);

        var result = await CreateStore().IsFileAlreadyProcessedAsync(jobId, "file.xml");

        Assert.True(result);
    }

    [Fact]
    public async Task IsFileAlreadyProcessedAsync_WhenDisabled_ReturnsFalseWithoutCallingRepository()
    {
        var result = await CreateStore(new IdempotencyOptions { EnableFileIdempotency = false })
            .IsFileAlreadyProcessedAsync(Guid.NewGuid(), "file.xml");

        Assert.False(result);
        _fileRepo.Verify(r => r.IsFileAlreadyProcessedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MarkFileAsProcessedAsync_WhenEnabled_DelegatesToRepository()
    {
        var jobId = Guid.NewGuid();
        _fileRepo.Setup(r => r.MarkFileAsProcessedAsync(jobId, "file.xml", true, It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        await CreateStore().MarkFileAsProcessedAsync(jobId, "file.xml", true);

        _fileRepo.Verify(r => r.MarkFileAsProcessedAsync(jobId, "file.xml", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkFileAsProcessedAsync_WhenDisabled_DoesNotCallRepository()
    {
        await CreateStore(new IdempotencyOptions { EnableFileIdempotency = false })
            .MarkFileAsProcessedAsync(Guid.NewGuid(), "file.xml", true);

        _fileRepo.Verify(r => r.MarkFileAsProcessedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── State transitions ──────────────────────────────────────────────────────

    [Fact]
    public async Task MarkRunningAsync_DelegatesToRepository()
    {
        var jobId = Guid.NewGuid();
        _jobRepo.Setup(r => r.SetTotalFilesAsync(jobId, 10, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateStore().MarkRunningAsync(jobId, 10);

        _jobRepo.Verify(r => r.SetTotalFilesAsync(jobId, 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkFilesProcessedAsync_DelegatesToRepository()
    {
        var jobId = Guid.NewGuid();
        _jobRepo.Setup(r => r.IncrementCountersAsync(jobId, 3, 2, 1, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateStore().MarkFilesProcessedAsync(jobId, 3, 2, 1);

        _jobRepo.Verify(r => r.IncrementCountersAsync(jobId, 3, 2, 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkFailedAsync_DelegatesToRepository()
    {
        var jobId = Guid.NewGuid();
        _jobRepo.Setup(r => r.SetCompletionStateAsync(jobId, DispatchJobState.Failed, "err", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateStore().MarkFailedAsync(jobId, "err");

        _jobRepo.Verify(r => r.SetCompletionStateAsync(jobId, DispatchJobState.Failed, "err", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkCompletedAsync_DelegatesToRepository()
    {
        var jobId = Guid.NewGuid();
        _jobRepo.Setup(r => r.SetCompletionStateAsync(jobId, DispatchJobState.Completed, null, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateStore().MarkCompletedAsync(jobId);

        _jobRepo.Verify(r => r.SetCompletionStateAsync(jobId, DispatchJobState.Completed, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkCancelledAsync_DelegatesToRepository()
    {
        var jobId = Guid.NewGuid();
        _jobRepo.Setup(r => r.SetCompletionStateAsync(jobId, DispatchJobState.Cancelled, null, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateStore().MarkCancelledAsync(jobId);

        _jobRepo.Verify(r => r.SetCompletionStateAsync(jobId, DispatchJobState.Cancelled, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryClaimJobAsync_DelegatesToRepository()
    {
        var jobId = Guid.NewGuid();
        _jobRepo.Setup(r => r.TryClaimJobAsync(jobId, "worker-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateStore().TryClaimJobAsync(jobId, "worker-1");

        Assert.True(result);
    }

    // ── Options defaults ───────────────────────────────────────────────────────

    [Fact]
    public void PersistenceOptions_Defaults_AreSet()
    {
        var opts = new PersistenceOptions();
        Assert.Equal("PostgreSQL", opts.Provider);
        Assert.NotEmpty(opts.ConnectionString);
        Assert.Equal("Persistence", PersistenceOptions.SectionName);
    }
}
