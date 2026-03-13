using Confluent.Kafka;
using EnterpriseApi.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace EnterpriseApi.Infrastructure.Messaging;

/// <summary>
/// Base class for all Kafka consumers. Each subclass specifies which topics to subscribe to
/// and handles specific event types.
///
/// CONSUMER GROUP: All instances of the same service share the same GroupId.
/// Kafka distributes partitions across group members — horizontal scaling is automatic.
/// Two pods of this API each consume different partitions, no message duplication.
///
/// AT-LEAST-ONCE vs EXACTLY-ONCE:
/// We use manual offset commit (EnableAutoCommit=false) to control exactly when
/// we acknowledge a message. The flow is: consume → process → commit offset.
/// If the process crashes between consume and commit, the message is redelivered.
/// This means your handlers must be IDEMPOTENT — processing the same message twice
/// must produce the same result as processing it once.
///
/// BACKGROUND SERVICE LIFETIME: IHostedService starts on app start and runs
/// until the app shuts down. The CancellationToken is triggered on SIGTERM/Ctrl+C.
/// </summary>
public abstract class KafkaConsumerService : BackgroundService
{
    protected readonly IServiceScopeFactory _scopeFactory;
    protected readonly ILogger _logger;
    private readonly string _groupId;
    private readonly string[] _topics;
    private readonly string _bootstrapServers;

    protected KafkaConsumerService(
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        string groupId,
        string[] topics,
        string bootstrapServers = "localhost:9092")
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _groupId = groupId;
        _topics = topics;
        _bootstrapServers = bootstrapServers;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Consumer} starting, subscribing to [{Topics}]",
            GetType().Name, string.Join(", ", _topics));

        var config = new ConsumerConfig
        {
            BootstrapServers = _bootstrapServers,
            GroupId = _groupId,
            // Read from earliest on first run — don't miss messages produced before startup
            AutoOffsetReset = AutoOffsetReset.Earliest,
            // Manual commit — we control exactly when we acknowledge processing
            EnableAutoCommit = false,
            // Heartbeat must arrive within 30s or the consumer is considered dead
            SessionTimeoutMs = 30_000,
            // How often to poll Kafka broker (prevents session timeout)
            HeartbeatIntervalMs = 3_000,
            // Max messages to fetch per poll — limits memory pressure
            MaxPollIntervalMs = 300_000
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, e) =>
                _logger.LogError("Kafka consumer error: {Code} {Reason}", e.Code, e.Reason))
            .SetPartitionsAssignedHandler((c, partitions) =>
                _logger.LogInformation("Partitions assigned: {Partitions}",
                    string.Join(", ", partitions)))
            .SetPartitionsRevokedHandler((c, partitions) =>
                _logger.LogInformation("Partitions revoked: {Partitions}",
                    string.Join(", ", partitions)))
            .Build();

        consumer.Subscribe(_topics);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result = null;
                try
                {
                    // Poll with a 1-second timeout — allows checking stoppingToken regularly
                    result = consumer.Consume(TimeSpan.FromSeconds(1));
                    if (result is null) continue; // Timeout — no message available

                    var eventType = result.Message.Headers
                        .TryGetLastBytes("eventType", out var typeBytes)
                        ? System.Text.Encoding.UTF8.GetString(typeBytes)
                        : result.Message.Key;

                    _logger.LogDebug("Consumed event {EventType} from {Topic}[{Partition}]@{Offset}",
                        eventType, result.Topic, result.Partition.Value, result.Offset.Value);

                    // Process in a new scope — consumer is Singleton, handlers need Scoped services
                    using var scope = _scopeFactory.CreateScope();
                    await ProcessMessageAsync(scope.ServiceProvider, result, eventType, stoppingToken);

                    // Commit AFTER successful processing — guarantees at-least-once
                    consumer.Commit(result);
                }
                catch (OperationCanceledException)
                {
                    break; // Graceful shutdown
                }
                catch (ConsumeException ex) when (ex.Error.IsFatal)
                {
                    _logger.LogCritical(ex, "Fatal Kafka consume error — stopping consumer");
                    break;
                }
                catch (Exception ex)
                {
                    // Non-fatal — log and continue. Do NOT commit so message is retried.
                    _logger.LogError(ex, "Error processing message from {Topic}. Will retry.",
                        result?.Topic ?? "unknown");

                    // Backoff before retrying — prevents tight error loop hammering the broker
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        finally
        {
            // Cleanly leave the consumer group — partitions reassigned immediately vs waiting for timeout
            consumer.Close();
            _logger.LogInformation("{Consumer} stopped", GetType().Name);
        }
    }

    protected abstract Task ProcessMessageAsync(IServiceProvider services,
        ConsumeResult<string, string> result, string eventType, CancellationToken ct);
}

// ─────────────────────────────────────────────────────────────────────────────
// ORDER EVENT CONSUMER
// Handles: OrderCreated → send confirmation notification
//          OrderCompleted → send receipt notification
//          OrderCancelled → send cancellation notification
// ─────────────────────────────────────────────────────────────────────────────
public class OrderEventConsumer : KafkaConsumerService
{
    public OrderEventConsumer(IServiceScopeFactory scopeFactory,
        ILogger<OrderEventConsumer> logger)
        : base(scopeFactory, logger, "enterprise-order-consumer",
            new[] { KafkaEventPublisher.Topics.Orders })
    { }

    protected override async Task ProcessMessageAsync(IServiceProvider services,
        ConsumeResult<string, string> result, string eventType, CancellationToken ct)
    {
        // Resolve scoped services from the DI scope created in the base class
        var notificationService = services.GetRequiredService<Application.Interfaces.INotificationService>();

        switch (eventType)
        {
            case "order.created":
                var orderCreated = JsonSerializer.Deserialize<OrderCreatedEvent>(result.Message.Value)!;
                await notificationService.SendAsync(
                    orderCreated.UserId, "Email",
                    $"Order Confirmed - {orderCreated.ProductName}",
                    $"Your order for {orderCreated.Quantity}x {orderCreated.ProductName} " +
                    $"(£{orderCreated.TotalAmount:F2}) has been placed. Order ID: {orderCreated.OrderId}",
                    ct);
                _logger.LogInformation("Order confirmation notification queued for {UserId}",
                    orderCreated.UserId);
                break;

            case "order.completed":
                var orderCompleted = JsonSerializer.Deserialize<OrderCompletedEvent>(result.Message.Value)!;
                await notificationService.SendAsync(
                    orderCompleted.UserId, "Email",
                    "Order Completed - Invoice Ready",
                    $"Your order has been completed. Invoice {orderCompleted.InvoiceId} " +
                    $"for £{orderCompleted.Amount:F2} is ready.",
                    ct);
                break;

            case "order.cancelled":
                var orderCancelled = JsonSerializer.Deserialize<OrderCancelledEvent>(result.Message.Value)!;
                await notificationService.SendAsync(
                    orderCancelled.UserId, "Email",
                    "Order Cancelled",
                    $"Your order {orderCancelled.OrderId} has been cancelled. " +
                    $"Reason: {orderCancelled.CancellationReason}",
                    ct);
                break;

            default:
                _logger.LogWarning("Unknown event type {EventType} — skipping", eventType);
                break;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// USER EVENT CONSUMER
// Handles: UserRegistered → send welcome email notification
// ─────────────────────────────────────────────────────────────────────────────
public class UserEventConsumer : KafkaConsumerService
{
    public UserEventConsumer(IServiceScopeFactory scopeFactory,
        ILogger<UserEventConsumer> logger)
        : base(scopeFactory, logger, "enterprise-user-consumer",
            new[] { KafkaEventPublisher.Topics.Users })
    { }

    protected override async Task ProcessMessageAsync(IServiceProvider services,
        ConsumeResult<string, string> result, string eventType, CancellationToken ct)
    {
        var notificationService = services.GetRequiredService<Application.Interfaces.INotificationService>();

        if (eventType == "user.registered")
        {
            var userRegistered = JsonSerializer.Deserialize<UserRegisteredEvent>(result.Message.Value)!;
            await notificationService.SendAsync(
                userRegistered.UserId, "Email",
                "Welcome to EnterpriseAPI!",
                $"Hi {userRegistered.FirstName}, welcome! Your account has been created.",
                ct);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// INVENTORY EVENT CONSUMER
// Handles: InventoryLowStock → alert admin users
// ─────────────────────────────────────────────────────────────────────────────
public class InventoryEventConsumer : KafkaConsumerService
{
    public InventoryEventConsumer(IServiceScopeFactory scopeFactory,
        ILogger<InventoryEventConsumer> logger)
        : base(scopeFactory, logger, "enterprise-inventory-consumer",
            new[] { KafkaEventPublisher.Topics.Inventory })
    { }

    protected override async Task ProcessMessageAsync(IServiceProvider services,
        ConsumeResult<string, string> result, string eventType, CancellationToken ct)
    {
        var logger = services.GetRequiredService<ILogger<InventoryEventConsumer>>();

        if (eventType == "inventory.low_stock")
        {
            var lowStock = JsonSerializer.Deserialize<InventoryLowStockEvent>(result.Message.Value)!;
            // In production: look up admin users and notify them
            logger.LogWarning(
                "LOW STOCK ALERT: Product {ProductName} ({ProductId}) has {CurrentStock} units " +
                "remaining (threshold: {Threshold})",
                lowStock.ProductName, lowStock.ProductId,
                lowStock.CurrentStock, lowStock.ThresholdLevel);
        }
    }
}
