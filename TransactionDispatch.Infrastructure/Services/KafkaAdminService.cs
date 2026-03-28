using System.Diagnostics.CodeAnalysis;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;
using TransactionDispatch.Application.Options;

namespace TransactionDispatch.Infrastructure.Services;

[ExcludeFromCodeCoverage(Justification = "Requires a real Kafka cluster.")]
public sealed class KafkaAdminService
{
    private readonly KafkaOptions _settings;

    public KafkaAdminService(IOptions<KafkaOptions> options)
    {
        _settings = options.Value;
    }

    public async Task CreateTopicIfNotExistsAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = _settings.BootstrapServers
        }).Build();

        try
        {
            await admin.CreateTopicsAsync(new[]
            {
                new TopicSpecification
                {
                    Name = _settings.Topic,
                    NumPartitions = _settings.NumPartitions,
                    ReplicationFactor = _settings.ReplicationFactor
                }
            });
        }
        catch (CreateTopicsException ex)
        {
            if (ex.Results.Any(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
                return;

            throw;
        }
    }
}