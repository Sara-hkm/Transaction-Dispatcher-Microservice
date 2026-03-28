using TransactionDispatch.Infrastructure;

namespace TransactionDispatch.Tests;

public sealed class JobCancellationRegistryTests
{
    [Fact]
    public void RegisterOrGet_Returns_Valid_Token_For_New_Job()
    {
        var registry = new JobCancellationRegistry();
        var jobId = Guid.NewGuid();
        using var appCts = new CancellationTokenSource();

        var token = registry.RegisterOrGet(jobId, appCts.Token);

        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void RegisterOrGet_Returns_Same_Token_For_Same_Job()
    {
        var registry = new JobCancellationRegistry();
        var jobId = Guid.NewGuid();
        using var appCts = new CancellationTokenSource();

        var token1 = registry.RegisterOrGet(jobId, appCts.Token);
        var token2 = registry.RegisterOrGet(jobId, appCts.Token);

        Assert.Equal(token1, token2);
    }

    [Fact]
    public void RegisterOrGet_Token_Is_Cancelled_When_AppToken_Cancelled()
    {
        var registry = new JobCancellationRegistry();
        var jobId = Guid.NewGuid();
        using var appCts = new CancellationTokenSource();

        var token = registry.RegisterOrGet(jobId, appCts.Token);
        Assert.False(token.IsCancellationRequested);

        appCts.Cancel();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Complete_Removes_Entry_And_Disposes_Token()
    {
        var registry = new JobCancellationRegistry();
        var jobId = Guid.NewGuid();
        using var appCts = new CancellationTokenSource();

        registry.RegisterOrGet(jobId, appCts.Token);
        registry.Complete(jobId);

        // Re-register with a new CTS — should get a fresh, uncancelled token
        using var appCts2 = new CancellationTokenSource();
        var freshToken = registry.RegisterOrGet(jobId, appCts2.Token);
        Assert.False(freshToken.IsCancellationRequested);
    }

    [Fact]
    public void Complete_Does_Nothing_For_Unknown_Job()
    {
        var registry = new JobCancellationRegistry();

        // Should not throw
        registry.Complete(Guid.NewGuid());
    }

    [Fact]
    public void Multiple_Jobs_Can_Be_Registered_Independently()
    {
        var registry = new JobCancellationRegistry();
        var jobId1 = Guid.NewGuid();
        var jobId2 = Guid.NewGuid();
        using var appCts1 = new CancellationTokenSource();
        using var appCts2 = new CancellationTokenSource();

        var token1 = registry.RegisterOrGet(jobId1, appCts1.Token);
        var token2 = registry.RegisterOrGet(jobId2, appCts2.Token);

        appCts1.Cancel();

        Assert.True(token1.IsCancellationRequested);
        Assert.False(token2.IsCancellationRequested);
    }
}
