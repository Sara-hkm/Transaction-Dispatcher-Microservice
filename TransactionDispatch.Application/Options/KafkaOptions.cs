namespace TransactionDispatch.Application.Options;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string Topic { get; set; } = "transactions-topic";
    public string ClientId { get; set; } = "transaction-dispatch-service";
    public string CompressionType { get; set; } = "snappy";
    public int LingerMs { get; set; } = 20;
    public int BatchSizeBytes { get; set; } = 131072;
    public int QueueBufferingMaxKbytes { get; set; } = 1048576;
    public int QueueBufferingMaxMessages { get; set; } = 1000000;
    public int MessageTimeoutMs { get; set; } = 120000;
    public bool EnableIdempotence { get; set; } = true;
    public int InFlightRequestsPerConnection { get; set; } = 5;
    public int NumPartitions { get; set; } = 6;
    public short ReplicationFactor { get; set; } = 1;
    /// <summary>
    /// Maximum file size in bytes that can be sent as a single Kafka message.
    /// Defaults to 1 MB — the Kafka broker default for message.max.bytes.
    /// Files exceeding this limit are skipped and counted as failed.
    /// </summary>
    public int MaxMessageSizeBytes { get; set; } = 1_000_000;
}
