using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TransactionDispatch.Application.Options;
using TransactionDispatch.Infrastructure;

namespace TransactionDispatch.Tests;

public sealed class KafkaTransactionDispatcherTests
{
    private static IOptions<KafkaOptions> DefaultOptions(string topic = "test-topic") =>
        Options.Create(new KafkaOptions { Topic = topic, BootstrapServers = "localhost:9092" });

    [Fact]
    public async Task DispatchAsync_Reads_File_And_Sends_Payload_To_Producer()
    {
        var content = "<?xml version=\"1.0\"?><root/>"u8.ToArray();
        var tempFile = Path.GetTempFileName();
        await File.WriteAllBytesAsync(tempFile, content);

        try
        {
            var producerMock = new Mock<IProducer<string, byte[]>>();
            producerMock
                .Setup(p => p.ProduceAsync("test-topic", It.IsAny<Message<string, byte[]>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult<string, byte[]>());

            var dispatcher = new KafkaTransactionDispatcher(
                DefaultOptions(), NullLogger<KafkaTransactionDispatcher>.Instance, producerMock.Object);

            var result = await dispatcher.DispatchAsync(tempFile, CancellationToken.None);

            Assert.True(result);
            producerMock.Verify(
                p => p.ProduceAsync("test-topic", It.Is<Message<string, byte[]>>(m => m.Value.SequenceEqual(content) && m.Key == Path.GetFileName(tempFile)), It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DispatchAsync_Returns_True_On_Successful_Produce()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllBytesAsync(tempFile, [0x01, 0x02]);

        try
        {
            var producerMock = new Mock<IProducer<string, byte[]>>();
            producerMock
                .Setup(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, byte[]>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult<string, byte[]>());

            var dispatcher = new KafkaTransactionDispatcher(
                DefaultOptions(), NullLogger<KafkaTransactionDispatcher>.Instance, producerMock.Object);

            var result = await dispatcher.DispatchAsync(tempFile, CancellationToken.None);

            Assert.True(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DispatchAsync_Propagates_Exception_From_Producer()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllBytesAsync(tempFile, [0x01]);

        try
        {
            var producerMock = new Mock<IProducer<string, byte[]>>();
            producerMock
                .Setup(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, byte[]>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ProduceException<string, byte[]>(
                    new Error(ErrorCode.Local_QueueFull),
                    new DeliveryResult<string, byte[]>()));

            var dispatcher = new KafkaTransactionDispatcher(
                DefaultOptions(), NullLogger<KafkaTransactionDispatcher>.Instance, producerMock.Object);

            await Assert.ThrowsAsync<ProduceException<string, byte[]>>(
                () => dispatcher.DispatchAsync(tempFile, CancellationToken.None));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DispatchAsync_Propagates_Cancellation()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllBytesAsync(tempFile, [0x01]);

        try
        {
            var producerMock = new Mock<IProducer<string, byte[]>>();
            producerMock
                .Setup(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, byte[]>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

            var dispatcher = new KafkaTransactionDispatcher(
                DefaultOptions(), NullLogger<KafkaTransactionDispatcher>.Instance, producerMock.Object);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => dispatcher.DispatchAsync(tempFile, cts.Token));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Dispose_Flushes_And_Disposes_Producer()
    {
        var producerMock = new Mock<IProducer<string, byte[]>>();

        var dispatcher = new KafkaTransactionDispatcher(
            DefaultOptions(), NullLogger<KafkaTransactionDispatcher>.Instance, producerMock.Object);

        dispatcher.Dispose();

        producerMock.Verify(p => p.Flush(It.IsAny<TimeSpan>()), Times.Once);
        producerMock.Verify(p => p.Dispose(), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_Returns_False_And_Skips_Producer_When_File_Exceeds_MaxSize()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllBytesAsync(tempFile, [0x01, 0x02]);

        try
        {
            var options = Options.Create(new KafkaOptions
            {
                Topic = "test-topic",
                BootstrapServers = "localhost:9092",
                MaxMessageSizeBytes = 1  // 1-byte limit — any real file will exceed this
            });

            var producerMock = new Mock<IProducer<string, byte[]>>();

            var dispatcher = new KafkaTransactionDispatcher(
                options, NullLogger<KafkaTransactionDispatcher>.Instance, producerMock.Object);

            var result = await dispatcher.DispatchAsync(tempFile, CancellationToken.None);

            Assert.False(result);
            producerMock.VerifyNoOtherCalls();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
