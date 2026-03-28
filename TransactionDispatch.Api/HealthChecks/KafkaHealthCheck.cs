using System.Diagnostics.CodeAnalysis;
using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TransactionDispatch.Application.Options;

namespace TransactionDispatch.Api.HealthChecks;

[ExcludeFromCodeCoverage(Justification = "Requires a live Kafka broker.")]
public sealed class KafkaHealthCheck(IOptions<KafkaOptions> kafkaOptions) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var adminClient = new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = kafkaOptions.Value.BootstrapServers
            }).Build();

            // GetMetadata is synchronous and blocks the calling thread; offload to the thread pool
            // so the health-check pipeline can honour the cancellation token.
            var metadata = await Task.Run(
                () => adminClient.GetMetadata(TimeSpan.FromSeconds(5)),
                cancellationToken);

            return metadata.Brokers.Count > 0
                ? HealthCheckResult.Healthy($"Kafka is healthy. {metadata.Brokers.Count} broker(s) available.")
                : HealthCheckResult.Unhealthy("No Kafka brokers are available.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Kafka connection failed.", ex);
        }
    }
}
