using System.Diagnostics.CodeAnalysis;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionDispatch.Application.Interfaces;
using TransactionDispatch.Application.Options;

namespace TransactionDispatch.Infrastructure;

public sealed class KafkaTransactionDispatcher : ITransactionDispatcher, IDisposable
{
    private readonly IOptions<KafkaOptions> _kafkaOptions;
    private readonly ILogger<KafkaTransactionDispatcher> _logger;
    private readonly IProducer<string, byte[]> _producer;

    [ExcludeFromCodeCoverage(Justification = "Requires a real Kafka broker to build the producer.")]
    private static CompressionType ParseCompression(string configured)
        => Enum.TryParse<CompressionType>(configured, true, out var parsed) ? parsed : CompressionType.Snappy;

    [ExcludeFromCodeCoverage(Justification = "Requires a real Kafka broker to build the producer.")]
    public KafkaTransactionDispatcher(
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<KafkaTransactionDispatcher> logger)
        : this(kafkaOptions, logger, BuildProducer(kafkaOptions.Value)) { }

    internal KafkaTransactionDispatcher(
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<KafkaTransactionDispatcher> logger,
        IProducer<string, byte[]> producer)
    {
        _kafkaOptions = kafkaOptions;
        _logger = logger;
        _producer = producer;
    }

    [ExcludeFromCodeCoverage(Justification = "Requires a real Kafka broker to build the producer.")]
    private static IProducer<string, byte[]> BuildProducer(KafkaOptions options) =>
        new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = options.ClientId,
            Acks = Acks.All,
            CompressionType = ParseCompression(options.CompressionType),
            LingerMs = options.LingerMs,
            BatchSize = options.BatchSizeBytes,
            QueueBufferingMaxKbytes = options.QueueBufferingMaxKbytes,
            QueueBufferingMaxMessages = options.QueueBufferingMaxMessages,
            MessageTimeoutMs = options.MessageTimeoutMs,
            EnableIdempotence = options.EnableIdempotence,
            MaxInFlight = options.InFlightRequestsPerConnection
        }).Build();

    public async Task<bool> DispatchAsync(string filePath, CancellationToken cancellationToken)
    {
        var maxBytes = _kafkaOptions.Value.MaxMessageSizeBytes;
        if (new FileInfo(filePath).Length > maxBytes)
        {
            _logger.LogWarning(
                "Skipping {FilePath}: file size exceeds MaxMessageSizeBytes ({Max} bytes)",
                filePath, maxBytes);
            return false;  // permanent failure — do not retry
        }

        var payload = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var result = await _producer.ProduceAsync(
            _kafkaOptions.Value.Topic,
            new Message<string, byte[]> { Key = Path.GetFileName(filePath), Value = payload },
            cancellationToken);

        _logger.LogDebug("Dispatched file {FilePath} to partition {Partition} offset {Offset}", filePath, result.Partition, result.Offset);

        return true;
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }
}
