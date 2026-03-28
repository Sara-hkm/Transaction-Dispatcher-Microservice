using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TransactionDispatch.Application;
using TransactionDispatch.Application.Interfaces;
using TransactionDispatch.Domain;
using TransactionDispatch.Domain.Enums;
using TransactionDispatch.Infrastructure;

namespace TransactionDispatch.Tests;

public sealed class DispatchServiceTests
{
    private readonly Mock<IDispatchJobStore> _jobStoreMock;
    private readonly DispatchService _dispatchService;

    public DispatchServiceTests()
    {
        _jobStoreMock = new Mock<IDispatchJobStore>();
        _dispatchService = new DispatchService(_jobStoreMock.Object, NullLogger<DispatchService>.Instance);
    }

    [Fact]
    public async Task DispatchTransactionsAsync_Should_Create_Job_And_Return_Id()
    {
        var request = new DispatchRequest("c:\\test", true);
        var expectedJob = new DispatchJob
        {
            JobId = Guid.NewGuid(),
            FolderPath = request.FolderPath,
            DeleteAfterSend = request.DeleteAfterSend,
            State = DispatchJobState.Queued
        };

        _jobStoreMock
            .Setup(x => x.CreateAsync(request.FolderPath, request.DeleteAfterSend, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedJob);

        var result = await _dispatchService.DispatchTransactionsAsync(request, CancellationToken.None);

        Assert.Equal(expectedJob.JobId, result);
        _jobStoreMock.Verify(x => x.CreateAsync(request.FolderPath, request.DeleteAfterSend, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetJobStatusAsync_Should_Return_Job_When_Exists()
    {
        var jobId = Guid.NewGuid();
        var expectedJob = new DispatchJob { JobId = jobId, FolderPath = "c:\\test", DeleteAfterSend = false };

        _jobStoreMock
            .Setup(x => x.GetJobAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedJob);

        var result = await _dispatchService.GetJobStatusAsync(jobId);

        Assert.Equal(expectedJob, result);
    }

    [Fact]
    public async Task GetJobStatusAsync_Should_Return_Null_When_Job_Not_Found()
    {
        var jobId = Guid.NewGuid();

        _jobStoreMock
            .Setup(x => x.GetJobAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DispatchJob?)null);

        var result = await _dispatchService.GetJobStatusAsync(jobId);

        Assert.Null(result);
    }

    [Fact]
    public async Task DispatchTransactionsAsync_NullRequest_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _dispatchService.DispatchTransactionsAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task DispatchTransactionsAsync_WhitespaceFolderPath_ThrowsArgumentException()
    {
        var request = new DispatchRequest("   ");
        await Assert.ThrowsAsync<ArgumentException>(
            () => _dispatchService.DispatchTransactionsAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task GetJobStatusAsync_EmptyJobId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _dispatchService.GetJobStatusAsync(Guid.Empty));
    }
}

