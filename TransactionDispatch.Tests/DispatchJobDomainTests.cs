using TransactionDispatch.Domain;
using TransactionDispatch.Domain.Enums;

namespace TransactionDispatch.Tests;

public sealed class DispatchJobDomainTests
{
    private static DispatchJob Make(int total = 0, int processed = 0) =>
        new()
        {
            JobId = Guid.NewGuid(),
            FolderPath = "/test",
            DeleteAfterSend = false,
            TotalFiles = total,
            ProcessedFiles = processed
        };

    [Fact]
    public void Progress_WhenTotalFilesIsZero_ReturnsZeroPercent()
        => Assert.Equal("0%", Make().Progress);

    [Fact]
    public void Progress_WhenAllFilesProcessed_Returns100Percent()
        => Assert.Equal("100%", Make(total: 10, processed: 10).Progress);

    [Fact]
    public void Progress_WhenHalfProcessed_Returns50Percent()
        => Assert.Equal("50%", Make(total: 10, processed: 5).Progress);

    [Fact]
    public void Progress_WhenOneOutOfThree_Returns33Percent()
        => Assert.Equal("33%", Make(total: 3, processed: 1).Progress);

    [Fact]
    public void Progress_WhenTwoOutOfThree_Returns67Percent()
        => Assert.Equal("67%", Make(total: 3, processed: 2).Progress);

    [Fact]
    public void State_DefaultValue_IsQueued()
        => Assert.Equal(DispatchJobState.Queued, Make().State);

    [Fact]
    public void StartedAt_DefaultValue_IsNull()
        => Assert.Null(Make().StartedAt);

    [Fact]
    public void CompletedAt_DefaultValue_IsNull()
        => Assert.Null(Make().CompletedAt);

    [Fact]
    public void Error_DefaultValue_IsNull()
        => Assert.Null(Make().Error);

    [Fact]
    public void ClaimedBy_DefaultValue_IsNull()
        => Assert.Null(Make().ClaimedBy);

    [Fact]
    public void State_CanBeSetToRunning()
    {
        var job = Make();
        job.State = DispatchJobState.Running;
        Assert.Equal(DispatchJobState.Running, job.State);
    }

    [Fact]
    public void StartedAt_CanBeSet()
    {
        var job = Make();
        var now = DateTimeOffset.UtcNow;
        job.StartedAt = now;
        Assert.Equal(now, job.StartedAt);
    }
}
