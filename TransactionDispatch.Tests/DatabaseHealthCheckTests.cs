using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using TransactionDispatch.Api.HealthChecks;
using TransactionDispatch.Infrastructure.Data;

namespace TransactionDispatch.Tests;

public sealed class DatabaseHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_WhenDatabaseIsReachable_ReturnsHealthy()
    {
        // Use SQLite in-memory so CanConnectAsync() always succeeds without a real DB server.
        var options = new DbContextOptionsBuilder<TransactionDispatchDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        var factoryMock = new Mock<IDbContextFactory<TransactionDispatchDbContext>>();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new TransactionDispatchDbContext(options));

        var check = new DatabaseHealthCheck(factoryMock.Object);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenCannotConnect_ReturnsUnhealthy()
    {
        // Use an invalid data source so CanConnectAsync() returns false without throwing.
        var options = new DbContextOptionsBuilder<TransactionDispatchDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        // Create a context whose underlying connection was never opened, so CanConnectAsync = false.
        var neverOpenedOptions = new DbContextOptionsBuilder<TransactionDispatchDbContext>()
            .UseSqlite("DataSource=file:does_not_exist?mode=ro",
                o => o.CommandTimeout(1))
            .Options;

        var factoryMock = new Mock<IDbContextFactory<TransactionDispatchDbContext>>();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new TransactionDispatchDbContext(neverOpenedOptions));

        var check = new DatabaseHealthCheck(factoryMock.Object);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        // SQLite read-only mode against a non-existent file either returns false or throws,
        // both of which map to Unhealthy.
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenFactoryThrows_ReturnsUnhealthy()
    {
        var factoryMock = new Mock<IDbContextFactory<TransactionDispatchDbContext>>();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated connection failure"));

        var check = new DatabaseHealthCheck(factoryMock.Object);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }
}
