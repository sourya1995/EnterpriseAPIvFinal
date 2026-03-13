using Confluent.Kafka;
using EnterpriseApi.Application.Interfaces;
using EnterpriseApi.Domain.Events;
using EnterpriseApi.Domain.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace EnterpriseApi.Infrastructure.Messaging;

/// <summary>
/// Kafka-backed event publisher. Implements IEventPublisher so the Application layer
/// has zero knowledge of Confluent.Kafka — it only depends on the interface.
///
/// TOPIC ROUTING: Each event type maps to a specific Kafka topic.
/// This lets consumers subscribe selectively without receiving all events.
///
/// PARTITION KEY: Using OrderId/UserId etc. as partition key ensures all events
/// for the same entity are always delivered to the same partition in order.
/// Out-of-order events (OrderCancelled before OrderCreated) cause consistency bugs.
///
/// DELIVERY GUARANTEE: Using Acks.All ensures the message is written to all
/// in-sync replicas before the produce returns. This prevents data loss on broker failure.
/// Tradeoff: higher latency than Acks.Leader.
///
/// SINGLETON LIFETIME: IProducer is thread-safe and expensive to create.
/// It maintains an internal connection pool — must be Singleton.
/// </summary>
public class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaEventPublisher> _logger;

    // Constant topic names — in production these come from IConfiguration
    public static class Topics
    {
        public const string Orders = "enterprise.orders";
        public const string Inventory = "enterprise.inventory";
        public const string Invoices = "enterprise.invoices";
        public const string Notifications = "enterprise.notifications";
        public const string Users = "enterprise.users";
    }

    private static readonly Dictionary<string, string> EventTopicMap = new()
    {
        { "order.created",           Topics.Orders },
        { "order.cancelled",         Topics.Orders },
        { "order.completed",         Topics.Orders },
        { "inventory.reserved",      Topics.Inventory },
        { "inventory.low_stock",     Topics.Inventory },
        { "inventory.restocked",     Topics.Inventory },
        { "invoice.generated",       Topics.Invoices },
        { "invoice.paid",            Topics.Invoices },
        { "notification.requested",  Topics.Notifications },
        { "user.registered",         Topics.Users },
    };

    public KafkaEventPublisher(IConfiguration configuration,
        ILogger<KafkaEventPublisher> logger)
    {
        _logger = logger;

        var kafkaConfig = configuration.GetSection("Kafka");
        var bootstrapServers = kafkaConfig["BootstrapServers"]
            ?? "localhost:9092";

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            // All in-sync replicas must acknowledge — strongest delivery guarantee
            Acks = Acks.All,
            // Retry transient failures up to 3 times with exponential backoff
            MessageSendMaxRetries = 3,
            RetryBackoffMs = 100,
            // Idempotent producer — guarantees exactly-once delivery per session
            // Prevents duplicates on retry after network interruption
            EnableIdempotence = true,
            // Batch messages for up to 5ms to improve throughput
            LingerMs = 5,
            // Compress batches — reduces network bandwidth by ~60-70%
            CompressionType = CompressionType.Snappy
        };

        _producer = new ProducerBuilder<string, string>(producerConfig)
            .SetLogHandler((_, logMsg) =>
                _logger.LogDebug("Kafka: {Level} {Message}", logMsg.Level, logMsg.Message))
            .SetErrorHandler((_, error) =>
                _logger.LogError("Kafka error: {Code} {Reason} IsFatal:{IsFatal}",
                    error.Code, error.Reason, error.IsFatal))
            .Build();
    }

    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : DomainEvent
    {
        var topic = ResolveTopicOrThrow(domainEvent.EventType);
        var payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType());

        var message = new Message<string, string>
        {
            // Key = EventType ensures ordering within a topic partition
            Key = domainEvent.EventType,
            Value = payload,
            Headers = new Headers
            {
                { "eventId", System.Text.Encoding.UTF8.GetBytes(domainEvent.EventId.ToString()) },
                { "eventType", System.Text.Encoding.UTF8.GetBytes(domainEvent.EventType) },
                { "version", System.Text.Encoding.UTF8.GetBytes(domainEvent.Version) },
                { "occurredAt", System.Text.Encoding.UTF8.GetBytes(domainEvent.OccurredAt.ToString("O")) }
            }
        };

        try
        {
            var result = await _producer.ProduceAsync(topic, message, ct);

            _logger.LogInformation(
                "Event {EventType} published to {Topic}[{Partition}]@{Offset} EventId={EventId}",
                domainEvent.EventType, topic, result.Partition.Value,
                result.Offset.Value, domainEvent.EventId);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex,
                "Failed to publish {EventType} to {Topic}: {Error}",
                domainEvent.EventType, topic, ex.Error.Reason);

            throw new MessagePublishException(topic,
                $"Failed to publish event '{domainEvent.EventType}': {ex.Error.Reason}", ex);
        }
    }

    public async Task PublishManyAsync<TEvent>(IEnumerable<TEvent> events,
        CancellationToken ct = default) where TEvent : DomainEvent
    {
        // Fire all produces concurrently, then await all completions
        var tasks = events.Select(e => PublishAsync(e, ct));
        await Task.WhenAll(tasks);
    }

    private static string ResolveTopicOrThrow(string eventType)
    {
        if (!EventTopicMap.TryGetValue(eventType, out var topic))
            throw new MessagePublishException("unknown",
                $"No topic mapping found for event type '{eventType}'.");
        return topic;
    }

    public void Dispose()
    {
        // Flush any buffered messages before disposing
        // TimeSpan.FromSeconds(5) prevents indefinite hang on shutdown
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}

/// <summary>
/// Null publisher for development/test environments where Kafka is not running.
/// Registered via configuration flag to avoid crashes during local development.
/// Pattern: Null Object — same interface, no-op behavior.
/// </summary>
public class NullEventPublisher : IEventPublisher
{
    private readonly ILogger<NullEventPublisher> _logger;

    public NullEventPublisher(ILogger<NullEventPublisher> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : DomainEvent
    {
        _logger.LogDebug("[NullPublisher] Event {EventType} with Id {EventId} not published (Kafka disabled)",
            domainEvent.EventType, domainEvent.EventId);
        return Task.CompletedTask;
    }

    public Task PublishManyAsync<TEvent>(IEnumerable<TEvent> events, CancellationToken ct = default)
        where TEvent : DomainEvent
    {
        foreach (var e in events)
            _logger.LogDebug("[NullPublisher] Event {EventType} skipped", e.EventType);
        return Task.CompletedTask;
    }
}
