using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using TransactionDispatch.Application.Options;
using TransactionDispatch.Infrastructure.Data;
using TransactionDispatch.Infrastructure.Extensions;
using TransactionDispatch.Infrastructure.Services;

namespace TransactionDispatch.IntegrationTests;

/// <summary>
/// Starts a real PostgreSQL container and a real Kafka container once for the entire test class.
/// Applies EF Core migrations, creates the Kafka topic, then starts the full application host
/// (including <see cref="DispatchBackgroundService"/>) so that integration tests exercise the
/// complete runtime path end-to-end.
/// </summary>
public sealed class SharedFixture : IAsyncLifetime
{
    public const string TestTopic = "integration-test-transactions";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder()
        .Build();

    private IHost _host = null!;

    public IServiceProvider Services => _host.Services;
    public string KafkaBootstrapServers { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Start both containers in parallel to reduce fixture cold-start time.
        await Task.WhenAll(_postgres.StartAsync(), _kafka.StartAsync());

        KafkaBootstrapServers = _kafka.GetBootstrapAddress();

        // Build in-memory configuration that points at the live containers.
        var config = new Dictionary<string, string?>
        {
            [$"{PersistenceOptions.SectionName}:ConnectionString"]              = _postgres.GetConnectionString(),

            [$"{KafkaOptions.SectionName}:BootstrapServers"]                    = KafkaBootstrapServers,
            [$"{KafkaOptions.SectionName}:Topic"]                               = TestTopic,
            [$"{KafkaOptions.SectionName}:CompressionType"]                     = "none",
            [$"{KafkaOptions.SectionName}:EnableIdempotence"]                   = "false",
            [$"{KafkaOptions.SectionName}:LingerMs"]                            = "0",

            [$"{DispatchOptions.SectionName}:PollIntervalSeconds"]              = "1",
            [$"{DispatchOptions.SectionName}:MaxParallelism"]                   = "4",
            [$"{DispatchOptions.SectionName}:RetryCount"]                       = "2",
            [$"{DispatchOptions.SectionName}:RetryDelayMilliseconds"]           = "50",
            [$"{DispatchOptions.SectionName}:ProgressSaveEvery"]                = "1",
            [$"{DispatchOptions.SectionName}:SupportedExtensions:0"]            = ".xml",

            [$"{IdempotencyOptions.SectionName}:EnableFileIdempotency"]         = "true",
            [$"{IdempotencyOptions.SectionName}:EnableFolderIdempotency"]       = "true",
            [$"{IdempotencyOptions.SectionName}:FolderIdempotencyWindowMinutes"] = "60",
        };

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(b => b.AddInMemoryCollection(config))
            .ConfigureServices((ctx, services) => services.AddTransactionDispatch(ctx.Configuration))
            .Build();

        // Apply EF Core migrations to the live PostgreSQL container.
        var dbFactory = _host.Services.GetRequiredService<IDbContextFactory<TransactionDispatchDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        // Create the Kafka topic before the background service starts polling.
        var kafkaAdmin = _host.Services.GetRequiredService<KafkaAdminService>();
        await kafkaAdmin.CreateTopicIfNotExistsAsync();

        // StartAsync launches DispatchBackgroundService and all other hosted services.
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(10));
        _host.Dispose();

        await _postgres.DisposeAsync();
        await _kafka.DisposeAsync();
    }
}
