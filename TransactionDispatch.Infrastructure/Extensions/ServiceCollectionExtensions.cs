using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using TransactionDispatch.Application;
using TransactionDispatch.Application.Interfaces;
using TransactionDispatch.Application.Options;
using TransactionDispatch.Infrastructure.Data;
using TransactionDispatch.Infrastructure.Repositories;
using TransactionDispatch.Infrastructure.Services;

namespace TransactionDispatch.Infrastructure.Extensions;

[ExcludeFromCodeCoverage]
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTransactionDispatch(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DispatchOptions>(configuration.GetSection(DispatchOptions.SectionName));
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));
        services.Configure<IdempotencyOptions>(configuration.GetSection(IdempotencyOptions.SectionName));

        var connectionString = configuration
            .GetSection(PersistenceOptions.SectionName)
            .GetValue<string>(nameof(PersistenceOptions.ConnectionString));

        services.AddDbContextFactory<TransactionDispatchDbContext>(opts =>
            opts.UseNpgsql(connectionString));

        services.AddScoped<IDispatchJobRepository, DispatchJobRepository>();
        services.AddScoped<IProcessedFileRepository, ProcessedFileRepository>();

        services.AddScoped<IDispatchService, DispatchService>();
        services.AddScoped<IDispatchJobStore, RelationalDispatchJobStore>();

        // Must be Singleton: holds a ConcurrentDictionary of live job CancellationTokenSources
        // shared across the background service and the entire app lifetime.
        services.AddSingleton<IJobCancellationRegistry, JobCancellationRegistry>();

        // Must be Singleton: wraps a Kafka IProducer / IAdminClient (expensive TCP connections),
        // with IDisposable lifetime tied to the application.
        services.AddSingleton<KafkaAdminService>();
        services.AddSingleton<ITransactionDispatcher, KafkaTransactionDispatcher>();

        services.AddHostedService<DispatchBackgroundService>();

        return services;
    }
}
