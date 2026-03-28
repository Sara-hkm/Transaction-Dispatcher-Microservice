using Microsoft.AspNetCore.Mvc;
using Moq;
using TransactionDispatch.Api.Controllers;
using TransactionDispatch.Application;
using TransactionDispatch.Application.Interfaces;
using TransactionDispatch.Domain;
using TransactionDispatch.Domain.Enums;

namespace TransactionDispatch.Tests;

public sealed class DispatchControllerTests
{
    private readonly Mock<IDispatchService> _serviceMock;
    private readonly DispatchController _controller;

    public DispatchControllerTests()
    {
        _serviceMock = new Mock<IDispatchService>();
        _controller = new DispatchController(_serviceMock.Object);
    }

    [Fact]
    public async Task DispatchTransactions_WhitespaceFolderPath_ReturnsBadRequest()
    {
        // In production the MVC pipeline validates [NotWhiteSpace] before the action runs.
        // In unit tests we simulate that by pre-populating ModelState.
        var request = new DispatchRequest("   ");
        _controller.ModelState.AddModelError("FolderPath", "folderPath must not be empty or whitespace.");

        var result = await _controller.DispatchTransactions(request, CancellationToken.None);

        var actionResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, actionResult.StatusCode);
    }

    [Fact]
    public async Task DispatchTransactions_ValidRequest_ReturnsAccepted()
    {
        var jobId = Guid.NewGuid();
        _serviceMock
            .Setup(s => s.DispatchTransactionsAsync(It.IsAny<DispatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        var request = new DispatchRequest("c:\\data", DeleteAfterSend: false);

        var result = await _controller.DispatchTransactions(request, CancellationToken.None);

        var actionResult = Assert.IsType<AcceptedResult>(result.Result);
        Assert.Equal(202, actionResult.StatusCode);
    }

    [Fact]
    public async Task DispatchTransactions_DuplicateFolder_ReturnsConflict()
    {
        _serviceMock
            .Setup(s => s.DispatchTransactionsAsync(It.IsAny<DispatchRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Folder was already dispatched recently."));

        var request = new DispatchRequest("c:\\data", DeleteAfterSend: false);

        var result = await _controller.DispatchTransactions(request, CancellationToken.None);

        var actionResult = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, actionResult.StatusCode);
    }

    [Fact]
    public async Task GetStatus_UnknownJobId_ReturnsNotFound()
    {
        var jobId = Guid.NewGuid();
        _serviceMock
            .Setup(s => s.GetJobStatusAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DispatchJob?)null);

        var result = await _controller.GetStatus(jobId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetStatus_CompletedJob_ReturnsOk()
    {
        var jobId = Guid.NewGuid();
        _serviceMock
            .Setup(s => s.GetJobStatusAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchJob
            {
                JobId = jobId,
                FolderPath = "c:\\data",
                DeleteAfterSend = false,
                State = DispatchJobState.Completed,
                TotalFiles = 3,
                ProcessedFiles = 3,
                SuccessfulFiles = 3,
                FailedFiles = 0
            });

        var result = await _controller.GetStatus(jobId, CancellationToken.None);

        var actionResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, actionResult.StatusCode);
    }

    [Fact]
    public async Task GetStatus_RunningJob_ReturnsOkWithRunningState()
    {
        var jobId = Guid.NewGuid();
        _serviceMock
            .Setup(s => s.GetJobStatusAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchJob
            {
                JobId = jobId,
                FolderPath = "c:\\data",
                DeleteAfterSend = false,
                State = DispatchJobState.Running,
                TotalFiles = 5,
                ProcessedFiles = 2,
                SuccessfulFiles = 2,
                FailedFiles = 0
            });

        var result = await _controller.GetStatus(jobId, CancellationToken.None);

        var actionResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, actionResult.StatusCode);
    }

    [Fact]
    public void DispatchRequest_ParameterlessConstructor_InitializesDefaults()
    {
        // Exercises the parameterless ctor used by model binding.
        var request = new DispatchRequest { FolderPath = "c:\\data" };
        Assert.Equal("c:\\data", request.FolderPath);
        Assert.False(request.DeleteAfterSend);
    }

    [Fact]
    public async Task GetStatus_EmptyGuid_ReturnsBadRequest()
    {
        var result = await _controller.GetStatus(Guid.Empty, CancellationToken.None);

        var actionResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, actionResult.StatusCode);
    }

}

