using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Diagnostics.CodeAnalysis;
using TransactionDispatch.Infrastructure.Data;
using TransactionDispatch.Infrastructure.Services;

namespace TransactionDispatch.Api.Extensions;

/// <summary>
/// Extension methods for <see cref="WebApplication"/> that run once at startup
/// to verify infrastructure readiness and apply pending migrations.
/// Excluded from code coverage: requires real infrastructure (PostgreSQL, Kafka);
/// verified by integration tests.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Verifies that the PostgreSQL server is reachable, applies any pending EF Core migrations
    /// (creating the database if it does not exist), and ensures the Kafka topic exists.
    /// Throws <see cref="InvalidOperationException"/> and prevents the application from starting
    /// if the database server cannot be reached.
    /// </summary>
    public static async Task InitialiseInfrastructureAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        await VerifyDatabaseServerAsync(app.Configuration, logger);
        await ApplyMigrationsAsync(scope.ServiceProvider, logger);
        await EnsureKafkaTopicAsync(scope.ServiceProvider);
    }

    private static async Task VerifyDatabaseServerAsync(IConfiguration configuration, ILogger logger)
    {
        var rawConnectionString = configuration["Persistence:ConnectionString"]
            ?? throw new InvalidOperationException("Persistence:ConnectionString is not configured.");

        // Connect to the always-present "postgres" maintenance database to check server reachability
        // without requiring the application database to already exist.
        var builder = new NpgsqlConnectionStringBuilder(rawConnectionString) { Database = "postgres" };

        try
        {
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            logger.LogInformation("PostgreSQL server is reachable at {Host}:{Port}.", builder.Host, builder.Port);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Cannot reach the PostgreSQL server at {Host}:{Port}. Verify the server is running and the connection string is correct.",
                builder.Host, builder.Port);
            throw new InvalidOperationException("PostgreSQL server is unreachable. See logs for details.", ex);
        }
    }

    private static async Task ApplyMigrationsAsync(IServiceProvider services, ILogger logger)
    {
        var factory = services.GetRequiredService<IDbContextFactory<TransactionDispatchDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        logger.LogInformation("Applying pending database migrations...");
        await db.Database.MigrateAsync();
        logger.LogInformation("Migrations applied successfully.");
    }

    private static async Task EnsureKafkaTopicAsync(IServiceProvider services)
    {
        var admin = services.GetRequiredService<KafkaAdminService>();
        await admin.CreateTopicIfNotExistsAsync();
    }
}
