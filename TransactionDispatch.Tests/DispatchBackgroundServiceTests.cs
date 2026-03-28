using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TransactionDispatch.Application;
using TransactionDispatch.Application.Interfaces;
using TransactionDispatch.Application.Options;
using TransactionDispatch.Domain;
using TransactionDispatch.Domain.Enums;
using TransactionDispatch.Infrastructure.Services;

namespace TransactionDispatch.Tests;

public sealed class DispatchBackgroundServiceTests
{
    private static DispatchBackgroundService CreateService(
        IDispatchJobStore store,
        IJobCancellationRegistry registry,
        ITransactionDispatcher dispatcher,
        DispatchOptions? options = null) =>
        new(
            CreateScopeFactory(store),
            registry,
            dispatcher,
            Options.Create(options ?? new DispatchOptions
            {
                MaxParallelism = 1,
                RetryCount = 1,
                RetryDelayMilliseconds = 0,
                ProgressSaveEvery = 1,
                SupportedExtensions = [".xml"]
            }),
            NullLogger<DispatchBackgroundService>.Instance);

    // Wraps a store mock inside a fake IServiceScopeFactory so the background service
    // can resolve IDispatchJobStore from a scope without needing a real DI container.
    private static IServiceScopeFactory CreateScopeFactory(IDispatchJobStore store)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IDispatchJobStore)))
            .Returns(store);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory
            .Setup(f => f.CreateScope())
            .Returns(scope.Object);

        return scopeFactory.Object;
    }

    private static void SetupOneShot(Mock<IDispatchJobStore> storeMock, Guid jobId, string folderPath, bool deleteAfterSend = false)
    {
        var job = new DispatchJob { JobId = jobId, FolderPath = folderPath, DeleteAfterSend = deleteAfterSend, State = DispatchJobState.Queued };
        var callCount = 0;
        storeMock
            .Setup(s => s.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (callCount++ == 0) return (IEnumerable<DispatchJob>)[job];
                return [];
            });
        storeMock
            .Setup(s => s.TryClaimJobAsync(jobId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        storeMock
            .Setup(s => s.IsFileAlreadyProcessedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        storeMock
            .Setup(s => s.MarkFileAsProcessedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task ProcessJob_FolderDoesNotExist_Marks_Failed()
    {
        var jobId = Guid.NewGuid();
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var storeMock = new Mock<IDispatchJobStore>();
        SetupOneShot(storeMock, jobId, missingPath);

        var dispatcherMock = new Mock<ITransactionDispatcher>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storeMock.Setup(s => s.MarkFailedAsync(jobId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

        var registryMock = new Mock<IJobCancellationRegistry>();
        registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
            .Returns<Guid, CancellationToken>((_, ct) => ct);

        var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object);
        await service.StartAsync(CancellationToken.None);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        storeMock.Verify(s =>
            s.MarkFailedAsync(jobId, It.Is<string>(m => m.Contains("does not exist")), It.IsAny<CancellationToken>()), Times.Once);
        dispatcherMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessJob_EmptyFolder_MarkRunning_Then_MarkCompleted()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object);
            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            storeMock.Verify(s => s.MarkRunningAsync(jobId, 0, It.IsAny<CancellationToken>()), Times.Once);
            storeMock.Verify(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
            dispatcherMock.VerifyNoOtherCalls();
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_SuccessfulDispatch_Updates_Counters_And_Marks_Completed()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "a.xml"), "<root/>");
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "b.xml"), "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            dispatcherMock.Setup(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object);
            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            storeMock.Verify(s => s.MarkRunningAsync(jobId, 2, It.IsAny<CancellationToken>()), Times.Once);
            storeMock.Verify(s => s.MarkFilesProcessedAsync(jobId, 1, 1, 0, It.IsAny<CancellationToken>()), Times.Exactly(2));
            storeMock.Verify(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_DeleteAfterSend_True_Deletes_File_On_Success()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var filePath = Path.Combine(tempDir.FullName, "delete-me.xml");
            await File.WriteAllTextAsync(filePath, "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName, deleteAfterSend: true);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            dispatcherMock.Setup(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object);
            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            Assert.False(File.Exists(filePath));
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_DeleteAfterSend_False_Keeps_File()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var filePath = Path.Combine(tempDir.FullName, "keep-me.xml");
            await File.WriteAllTextAsync(filePath, "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName, deleteAfterSend: false);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            dispatcherMock.Setup(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object);
            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            Assert.True(File.Exists(filePath));
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_DispatcherReturnsFalse_Increments_FailedCounter()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "bad.xml"), "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            dispatcherMock.Setup(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object);
            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            storeMock.Verify(s => s.MarkFilesProcessedAsync(jobId, 1, 0, 1, It.IsAny<CancellationToken>()), Times.Once);
            storeMock.Verify(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_DispatcherThrows_Retries_Then_Fails_File()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "error.xml"), "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            dispatcherMock.Setup(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("disk error"));

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object,
                new DispatchOptions { MaxParallelism = 1, RetryCount = 3, RetryDelayMilliseconds = 0, ProgressSaveEvery = 1, SupportedExtensions = [".xml"] });

            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            dispatcherMock.Verify(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
            storeMock.Verify(s => s.MarkFilesProcessedAsync(jobId, 1, 0, 1, It.IsAny<CancellationToken>()), Times.Once);
            storeMock.Verify(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_DispatcherSucceedsOnRetry_Marks_Success()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "retry.xml"), "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            // First call throws (transient error) — second call succeeds.
            // Only exceptions trigger retries; a false return is a permanent failure.
            var callCount = 0;
            var dispatcherMock = new Mock<ITransactionDispatcher>();
            dispatcherMock.Setup(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    if (++callCount < 2) throw new IOException("transient");
                    return Task.FromResult(true);
                });

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object,
                new DispatchOptions { MaxParallelism = 1, RetryCount = 3, RetryDelayMilliseconds = 0, ProgressSaveEvery = 1, SupportedExtensions = [".xml"] });

            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            storeMock.Verify(s => s.MarkFilesProcessedAsync(jobId, 1, 1, 0, It.IsAny<CancellationToken>()), Times.Once);
            storeMock.Verify(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_DispatcherReturnsFalse_DoesNotRetry()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "perm.xml"), "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            dispatcherMock.Setup(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object,
                new DispatchOptions { MaxParallelism = 1, RetryCount = 3, RetryDelayMilliseconds = 0, ProgressSaveEvery = 1, SupportedExtensions = [".xml"] });

            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            // false is a permanent failure — dispatcher must be called exactly once, no retries
            dispatcherMock.Verify(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            storeMock.Verify(s => s.MarkFilesProcessedAsync(jobId, 1, 0, 1, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_WithPreCancelledToken_Marks_Cancelled()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "c.xml"), "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCancelledAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            using var preCancelledCts = new CancellationTokenSource();
            preCancelledCts.Cancel();
            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns(preCancelledCts.Token);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object);
            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            storeMock.Verify(s => s.MarkCancelledAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_ProgressSaveEvery_Batches_Updates()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            for (var i = 0; i < 4; i++)
                await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, $"f{i}.xml"), "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            dispatcherMock.Setup(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object,
                new DispatchOptions { MaxParallelism = 1, RetryCount = 1, RetryDelayMilliseconds = 0, ProgressSaveEvery = 2, SupportedExtensions = [".xml"] });

            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            storeMock.Verify(s => s.MarkFilesProcessedAsync(jobId, 2, 2, 0, It.IsAny<CancellationToken>()), Times.Exactly(2));
            storeMock.Verify(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_NonXmlFiles_Are_Ignored()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "data.csv"), "1,2,3");
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "data.txt"), "text");
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "data.xml"), "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            dispatcherMock.Setup(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object);
            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            storeMock.Verify(s => s.MarkRunningAsync(jobId, 1, It.IsAny<CancellationToken>()), Times.Once);
            dispatcherMock.Verify(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_UnexpectedException_Marks_Failed()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "x.xml"), "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            storeMock.Setup(s => s.MarkRunningAsync(jobId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("boom"));

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkFailedAsync(jobId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object);
            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            storeMock.Verify(s => s.MarkFailedAsync(jobId, "boom", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task TryClaimJob_Returns_False_Job_Is_Skipped()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "skip.xml"), "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            // Override the claim to return false (another instance already claimed it)
            storeMock
                .Setup(s => s.TryClaimJobAsync(jobId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            // After the skipped job, the second poll returns empty and service will idle,
            // so we need a way to know it finished — use a short-lived CTS.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            var registryMock = new Mock<IJobCancellationRegistry>();

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object);
            await service.StartAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(1)); // give the loop one iteration
            await service.StopAsync(CancellationToken.None);

            storeMock.Verify(s => s.TryClaimJobAsync(jobId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            storeMock.Verify(s => s.MarkRunningAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
            storeMock.Verify(s => s.MarkCompletedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
            storeMock.Verify(s => s.MarkFailedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            dispatcherMock.VerifyNoOtherCalls();
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_SuccessfulDispatch_Marks_File_As_Processed()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "a.xml"), "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            dispatcherMock.Setup(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object);
            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            storeMock.Verify(s => s.MarkFileAsProcessedAsync(jobId, It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_AlreadyProcessedFile_IsSkipped()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "dup.xml"), "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            // override: file already processed
            storeMock
                .Setup(s => s.IsFileAlreadyProcessedAsync(jobId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object);
            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            dispatcherMock.Verify(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            storeMock.Verify(s => s.MarkFileAsProcessedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
            storeMock.Verify(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_WhenShutdownDuringErrorDelay_ExitsCleanly()
    {
        // Simulates: poll throws, service enters error-delay, host shuts down mid-delay.
        // The inner catch (OperationCanceledException) { break; } must fire so the loop
        // exits cleanly instead of propagating the cancellation as an unhandled exception.
        var storeMock = new Mock<IDispatchJobStore>();
        var errorReachedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        storeMock
            .Setup(s => s.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                errorReachedTcs.TrySetResult();
                throw new InvalidOperationException("Simulated DB outage");
            });

        var service = CreateService(
            storeMock.Object,
            Mock.Of<IJobCancellationRegistry>(),
            Mock.Of<ITransactionDispatcher>(),
            new DispatchOptions
            {
                MaxParallelism = 1, RetryCount = 1, RetryDelayMilliseconds = 0,
                ProgressSaveEvery = 1, SupportedExtensions = [".xml"],
                PollIntervalSeconds = 60  // long enough that StopAsync fires first
            });

        await service.StartAsync(CancellationToken.None);
        // Wait until the service has thrown and entered the error-delay.
        await errorReachedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50); // give it a moment to reach Task.Delay

        // StopAsync cancels stoppingToken → Task.Delay throws OperationCanceledException
        // → inner catch fires → loop breaks → ExecuteAsync returns cleanly.
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExecuteAsync_GetPendingJobsAsyncReturnsNull_TreatsAsEmpty()
    {
        // Verifies the null-coalescing guard: pendingJobs ?? []
        var callCount = 0;
        var secondCycleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var storeMock = new Mock<IDispatchJobStore>();
        storeMock
            .Setup(s => s.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (callCount++ == 0)
                    return null!;  // first cycle returns null

                secondCycleTcs.TrySetResult();
                return Array.Empty<DispatchJob>();
            });

        var service = CreateService(
            storeMock.Object,
            Mock.Of<IJobCancellationRegistry>(),
            Mock.Of<ITransactionDispatcher>(),
            new DispatchOptions
            {
                MaxParallelism = 1, RetryCount = 1, RetryDelayMilliseconds = 0,
                ProgressSaveEvery = 1, SupportedExtensions = [".xml"],
                PollIntervalSeconds = 1
            });

        await service.StartAsync(CancellationToken.None);
        await secondCycleTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.True(callCount >= 2, "Should have polled at least twice without crashing");
    }

    [Fact]
    public async Task ProcessJob_TrailingFiles_FlushedByExplicitFlushPendingAsync()
    {
        // 3 files with ProgressSaveEvery=2: file 1+2 → inline batch flush;
        // file 3 remains in pending → explicit FlushPendingAsync() at end must flush it.
        // This covers the non-empty code path inside FlushPendingAsync.
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            for (var i = 0; i < 3; i++)
                await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, $"f{i}.xml"), "<root/>");

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            dispatcherMock.Setup(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            // ProgressSaveEvery=2, 3 files → 1 inline batch flush (2 files) +
            // 1 explicit trailing flush (1 file) via FlushPendingAsync()
            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object,
                new DispatchOptions
                {
                    MaxParallelism = 1, RetryCount = 1, RetryDelayMilliseconds = 0,
                    ProgressSaveEvery = 2, SupportedExtensions = [".xml"]
                });

            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            // MarkFilesProcessedAsync must be called twice: once inline (2 files) +
            // once via FlushPendingAsync for the trailing file.
            storeMock.Verify(
                s => s.MarkFilesProcessedAsync(jobId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            storeMock.Verify(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessJob_DeleteAfterSend_FileDeletionFails_JobStillCompletes()
    {
        // Exercises TryDeleteFile's catch block: deletion is attempted on a read-only file,
        // the exception is swallowed and logged, and the job completes normally.
        var tempDir = Directory.CreateTempSubdirectory();
        var filePath = Path.Combine(tempDir.FullName, "locked.xml");
        try
        {
            await File.WriteAllTextAsync(filePath, "<root/>");
            // Make the file read-only so File.Delete throws UnauthorizedAccessException.
            File.SetAttributes(filePath, FileAttributes.ReadOnly);

            var jobId = Guid.NewGuid();
            var storeMock = new Mock<IDispatchJobStore>();
            SetupOneShot(storeMock, jobId, tempDir.FullName, deleteAfterSend: true);

            var dispatcherMock = new Mock<ITransactionDispatcher>();
            dispatcherMock.Setup(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storeMock.Setup(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()))
                .Callback(() => tcs.TrySetResult()).Returns(Task.CompletedTask);

            var registryMock = new Mock<IJobCancellationRegistry>();
            registryMock.Setup(r => r.RegisterOrGet(jobId, It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((_, ct) => ct);

            var service = CreateService(storeMock.Object, registryMock.Object, dispatcherMock.Object);
            await service.StartAsync(CancellationToken.None);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            // File must still exist (deletion failed) but job must still complete.
            Assert.True(File.Exists(filePath));
            storeMock.Verify(s => s.MarkCompletedAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            // Restore attribute so cleanup can delete the file.
            if (File.Exists(filePath))
                File.SetAttributes(filePath, FileAttributes.Normal);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenPollStoreThrows_ServiceSurvivesAndContinues()
    {
        // Simulates a transient DB outage on the first poll cycle.
        // The background service must catch the exception, log it, and retry rather
        // than crashing the hosted service.
        var storeMock = new Mock<IDispatchJobStore>();
        var callCount = 0;
        var secondCycleReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        storeMock
            .Setup(s => s.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (callCount++ == 0)
                    throw new InvalidOperationException("Simulated DB outage");

                // Signal that the service survived the fault and reached a second cycle.
                secondCycleReached.TrySetResult();
                return Array.Empty<DispatchJob>();
            });

        var dispatcherMock = new Mock<ITransactionDispatcher>();
        var registryMock = new Mock<IJobCancellationRegistry>();
        var service = CreateService(
            storeMock.Object,
            registryMock.Object,
            dispatcherMock.Object,
            new DispatchOptions
            {
                MaxParallelism = 1,
                RetryCount = 1,
                RetryDelayMilliseconds = 0,
                ProgressSaveEvery = 1,
                SupportedExtensions = [".xml"],
                PollIntervalSeconds = 1   // keep test fast; 1 s is min after fault
            });

        await service.StartAsync(CancellationToken.None);

        // The service must reach the second poll cycle within a reasonable timeout.
        await secondCycleReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await service.StopAsync(CancellationToken.None);

        Assert.True(callCount >= 2, "GetPendingJobsAsync should have been called at least twice");
    }
}
