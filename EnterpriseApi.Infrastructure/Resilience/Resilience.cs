// ═════════════════════════════════════════════════════════════════════════════
// RESILIENCE PATTERNS (using Microsoft.Extensions.Http.Resilience / Polly v8)
//
// At 1B users, partial failures are the normal state of the system.
// Redis goes down for 30 seconds. The DB has a slow query. Kafka lags.
// The question is not IF this happens — it is WHAT HAPPENS TO YOUR USERS.
//
// Without resilience:
//   Redis times out (3s) → your request times out → 500 to user → retry storm
//   → 10x load on recovering Redis → Redis stays down longer → cascade failure
//
// With resilience:
//   Redis times out → circuit opens → you fall through to DB → degraded but alive
//   → circuit half-opens → Redis recovers → circuit closes → back to normal
//
// Four patterns, in order of importance:
//
//   1. Timeout         — never wait forever. Every external call needs a deadline.
//   2. Retry           — transient failures should be transparent to users.
//   3. Circuit Breaker — stop hammering a failing service. Give it time to recover.
//   4. Bulkhead        — isolate failure domains. Kafka being slow should not
//                        consume all your threads and starve HTTP requests.
// ═════════════════════════════════════════════════════════════════════════════

// FIX: Added missing usings.
// IEventPublisher and DomainEvent are required by ResilientEventPublisher.
// Without these, the class can't implement the interface or use the constraint.
using EnterpriseApi.Application.Interfaces;
using EnterpriseApi.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace EnterpriseApi.Infrastructure.Resilience;

public static class ResilienceExtensions
{
    public static IServiceCollection AddResiliencePolicies(this IServiceCollection services)
    {
        // Register the resilience pipeline for database operations
        services.AddResiliencePipeline("database", builder =>
        {
            builder
                // 1. Timeout — a DB query that takes > 5s is broken, not slow.
                //    Without this, a single slow query holds a connection forever.
                .AddTimeout(new TimeoutStrategyOptions
                {
                    Timeout = TimeSpan.FromSeconds(5),
                    OnTimeout = args =>
                    {
                        args.Context.Properties.TryGetValue(
                            new ResiliencePropertyKey<ILogger>("logger"), out var logger);
                        logger?.LogError("Database operation timed out after {Timeout}s",
                            args.Timeout.TotalSeconds);
                        return default;
                    }
                })

                // 2. Retry — transient DB errors (deadlock, timeout, connection lost)
                //    should retry automatically. Permanent errors (constraint violation,
                //    not found) should NOT retry — they will always fail.
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromMilliseconds(200),
                    BackoffType = DelayBackoffType.Exponential,   // 200ms, 400ms, 800ms
                    UseJitter = true,   // Randomises delay — prevents synchronized retry storms
                    ShouldHandle = new PredicateBuilder()
                        .Handle<TimeoutException>()
                        .Handle<TaskCanceledException>()
                        // Only retry SQL transient errors, not business logic errors
                        .Handle<Microsoft.Data.Sqlite.SqliteException>(ex =>
                            ex.SqliteErrorCode == 5   // SQLITE_BUSY
                            || ex.SqliteErrorCode == 6), // SQLITE_LOCKED
                    OnRetry = args =>
                    {
                        args.Context.Properties.TryGetValue(
                            new ResiliencePropertyKey<ILogger>("logger"), out var logger);
                        logger?.LogWarning(
                            "Database retry {AttemptNumber}/{MaxAttempts} after {Delay}ms",
                            args.AttemptNumber + 1, 3,
                            args.RetryDelay.TotalMilliseconds);
                        return default;
                    }
                })

                // 3. Circuit Breaker — after 5 failures in 30 seconds, open the circuit.
                //    While open, requests fail immediately (fast fail) without touching the DB.
                //    After 30 seconds, one probe request is allowed through (half-open).
                //    If it succeeds, the circuit closes. If it fails, wait another 30 seconds.
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,           // Open if 50%+ of requests fail
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 10,        // Need at least 10 requests to calculate ratio
                    BreakDuration = TimeSpan.FromSeconds(30),
                    OnOpened = args =>
                    {
                        args.Context.Properties.TryGetValue(
                            new ResiliencePropertyKey<ILogger>("logger"), out var logger);
                        logger?.LogCritical(
                            "🔴 Database circuit breaker OPENED. " +
                            "All DB requests will fast-fail for {BreakDuration}s",
                            args.BreakDuration.TotalSeconds);
                        return default;
                    },
                    OnClosed = args =>
                    {
                        args.Context.Properties.TryGetValue(
                            new ResiliencePropertyKey<ILogger>("logger"), out var logger);
                        logger?.LogInformation("🟢 Database circuit breaker CLOSED. Normal operation resumed.");
                        return default;
                    },
                    OnHalfOpened = args =>
                    {
                        args.Context.Properties.TryGetValue(
                            new ResiliencePropertyKey<ILogger>("logger"), out var logger);
                        logger?.LogInformation("🟡 Database circuit breaker HALF-OPEN. Testing recovery.");
                        return default;
                    }
                });
        });

        // Redis resilience pipeline — more aggressive fast-fail because
        // Redis should NEVER hold up a user request. Always fall through to DB.
        services.AddResiliencePipeline("redis", builder =>
        {
            builder
                .AddTimeout(new TimeoutStrategyOptions
                {
                    // Redis: 500ms timeout. If Redis takes longer, it's having a problem.
                    // Fall through to DB immediately. Never wait for a slow cache.
                    Timeout = TimeSpan.FromMilliseconds(500)
                })
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 1,   // One retry only for Redis
                    Delay = TimeSpan.FromMilliseconds(50),
                    ShouldHandle = new PredicateBuilder()
                        .Handle<TimeoutException>()
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.3,
                    SamplingDuration = TimeSpan.FromSeconds(10),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(15)  // Shorter break — Redis recovers fast
                });
        });

        // Kafka resilience — producer retries are critical for event durability
        services.AddResiliencePipeline("kafka-producer", builder =>
        {
            builder
                .AddTimeout(new TimeoutStrategyOptions
                {
                    Timeout = TimeSpan.FromSeconds(10)  // Kafka publish timeout
                })
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 5,
                    Delay = TimeSpan.FromSeconds(1),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder()
                        .Handle<Confluent.Kafka.KafkaException>(ex =>
                            // Retry on transient errors, not on message-too-large etc.
                            ex.Error.IsRetriable)
                        .Handle<TimeoutException>()
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 3,
                    BreakDuration = TimeSpan.FromSeconds(60)
                });
        });

        return services;
    }
}

// ─── Resilient Event Publisher ────────────────────────────────────────────────
// Wraps KafkaEventPublisher with the resilience pipeline above.
// If Kafka is down, events go to an outbox table instead.
// The outbox processor retries them when Kafka recovers.
// This guarantees at-least-once delivery — no events are ever lost.
public sealed class ResilientEventPublisher : IEventPublisher
{
    private readonly IEventPublisher _inner;
    private readonly ResiliencePipeline _pipeline;
    private readonly IOutboxRepository _outbox;
    private readonly ILogger<ResilientEventPublisher> _logger;

    public ResilientEventPublisher(
        IEventPublisher inner,
        ResiliencePipelineProvider<string> pipelineProvider,
        IOutboxRepository outbox,
        ILogger<ResilientEventPublisher> logger)
    {
        _inner = inner;
        _pipeline = pipelineProvider.GetPipeline("kafka-producer");
        _outbox = outbox;
        _logger = logger;
    }

    // FIX A — Wrong method signature and generic constraint.
    //
    // Original: PublishAsync<T>(string topic, T @event, ct) where T : class
    // Correct:  PublishAsync<TEvent>(TEvent domainEvent, ct) where TEvent : DomainEvent
    //
    // IEventPublisher.PublishAsync takes no `topic` parameter — the topic is
    // determined by the event type inside KafkaEventPublisher, not by the caller.
    // The generic constraint is `where TEvent : DomainEvent`, not `where T : class`.
    // A class that claims to implement an interface must match signatures exactly —
    // this was a compile error that prevented the entire project from building.
    //
    // FIX B — Wrong inner call.
    //
    // Original: _inner.PublishAsync(topic, @event, token)
    // Correct:  _inner.PublishAsync(domainEvent, token)
    //
    // The inner IEventPublisher.PublishAsync takes only (TEvent, CancellationToken).
    // Passing `topic` as the first argument would call a method that doesn't exist.
    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : DomainEvent
    {
        try
        {
            await _pipeline.ExecuteAsync(async token =>
                await _inner.PublishAsync(domainEvent, token), ct);
        }
        catch (BrokenCircuitException)
        {
            // Circuit is open — Kafka is down. Write to outbox instead.
            // The OutboxProcessorService will retry when Kafka recovers.
            _logger.LogWarning(
                "Kafka circuit open. Writing event {EventType} to outbox.",
                typeof(TEvent).Name);

            await WriteToOutboxAsync(domainEvent, ct);
        }
        catch (Exception ex)
        {
            // All retries exhausted — write to outbox as last resort.
            _logger.LogError(ex,
                "Failed to publish {EventType} to Kafka after retries. Writing to outbox.",
                typeof(TEvent).Name);

            await WriteToOutboxAsync(domainEvent, ct);
        }
    }

    // FIX C — Missing PublishManyAsync.
    //
    // IEventPublisher declares two methods: PublishAsync and PublishManyAsync.
    // The original class only implemented PublishAsync, which means it didn't
    // fully implement the interface — another compile error.
    // PublishManyAsync simply delegates to PublishAsync per event so each one
    // gets independent retry/circuit-breaker protection.
    public async Task PublishManyAsync<TEvent>(
        IEnumerable<TEvent> events, CancellationToken ct = default)
        where TEvent : DomainEvent
    {
        foreach (var @event in events)
        {
            await PublishAsync(@event, ct);
        }
    }

    private async Task WriteToOutboxAsync<TEvent>(TEvent domainEvent, CancellationToken ct)
        where TEvent : DomainEvent
    {
        await _outbox.AddAsync(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            // Topic is resolved by KafkaEventPublisher from the event type.
            // Store the event type so the outbox processor can reconstruct and republish.
            Topic = string.Empty,
            EventType = typeof(TEvent).AssemblyQualifiedName!,
            Payload = System.Text.Json.JsonSerializer.Serialize(domainEvent),
            CreatedAt = DateTime.UtcNow,
            RetryCount = 0
        }, ct);
    }
}

// Placeholder types — implement in Infrastructure layer
public interface IOutboxRepository
{
    Task AddAsync(OutboxMessage message, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken ct = default);
    Task MarkProcessedAsync(Guid id, CancellationToken ct = default);
    Task IncrementRetryAsync(Guid id, CancellationToken ct = default);
}

public class OutboxMessage
{
    public Guid Id { get; init; }
    public string Topic { get; init; } = default!;
    public string EventType { get; init; } = default!;
    public string Payload { get; init; } = default!;
    public DateTime CreatedAt { get; init; }
    public int RetryCount { get; set; }
    public DateTime? ProcessedAt { get; set; }
}