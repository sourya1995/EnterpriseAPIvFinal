// ═════════════════════════════════════════════════════════════════════════════
// OBSERVABILITY: THE THREE PILLARS
//
// At 1B users, you cannot debug by reading logs manually.
// You need systems that tell you what is wrong before your users do.
//
// Pillar 1 — Structured Logging (Serilog → CloudWatch/Elasticsearch)
//   Every log line is a queryable JSON document, not a string.
//   "Order {OrderId} failed" + { OrderId: "abc" } = searchable, aggregatable.
//
// Pillar 2 — Metrics (Prometheus → Grafana)
//   Counters, histograms, gauges on business and infrastructure events.
//   "Orders per second", "p99 latency", "cache hit rate", "DB pool saturation".
//
// Pillar 3 — Distributed Tracing (OpenTelemetry → Jaeger/X-Ray)
//   Every request gets a TraceId. Every service it touches adds a span.
//   When an order fails, you can see the entire call tree across all services.
//
// Rule: If you can't measure it, you can't improve it.
//       If you can't trace it, you can't debug it at 3AM.
// ═════════════════════════════════════════════════════════════════════════════

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EnterpriseApi.Infrastructure.Observability;

// ─── Custom Metrics ──────────────────────────────────────────────────────────
// These are the metrics you'll have on your Grafana dashboard.
// Each one answers a specific question you'll ask during an incident.
public sealed class EnterpriseApiMetrics : IDisposable
{
    // The Meter is the factory for all instruments in this service.
    // Name matches your service name in Prometheus/Grafana.
    private readonly Meter _meter = new("EnterpriseApi", "1.0.0");

    // ── Order Metrics ──────────────────────────────────────────────────────
    // Q: How many orders are we processing per second right now?
    private readonly Counter<long> _ordersCreated;

    // Q: What is the distribution of order processing time?
    //    p50, p95, p99 matter more than average for user experience.
    private readonly Histogram<double> _orderProcessingMs;

    // Q: What is our order failure rate? (Should be near zero)
    private readonly Counter<long> _ordersFailed;

    // Q: How much revenue are we generating per minute?
    private readonly Counter<double> _revenueTotal;

    // ── Cache Metrics ──────────────────────────────────────────────────────
    // Q: What is our cache hit rate? (Should be 90%+ for product catalog)
    //    If hit rate drops, either TTL is too short or invalidation is too aggressive.
    private readonly Counter<long> _cacheHits;
    private readonly Counter<long> _cacheMisses;

    // ── Database Metrics ───────────────────────────────────────────────────
    // Q: Are we saturating the DB connection pool?
    //    If this approaches pool size (e.g. 100), connections will queue.
    private readonly ObservableGauge<int> _dbConnectionsActive;

    // Q: How long do DB queries take? p99 > 100ms = investigate.
    private readonly Histogram<double> _dbQueryMs;

    // ── HTTP Metrics ────────────────────────────────────────────────────────
    // Q: What is our error rate per endpoint? Any endpoint with >0.1% errors needs attention.
    private readonly Counter<long> _httpRequests;
    private readonly Histogram<double> _httpRequestMs;

    // ── Kafka Metrics ──────────────────────────────────────────────────────
    // Q: Are we falling behind on event processing? Consumer lag > 0 = investigate.
    private readonly Counter<long> _eventsPublished;
    private readonly Counter<long> _eventsConsumed;
    private readonly Counter<long> _eventsFailedPublish;

    public EnterpriseApiMetrics()
    {
        _ordersCreated = _meter.CreateCounter<long>(
            "enterprise_api.orders.created",
            unit: "{orders}",
            description: "Total number of orders created");

        _orderProcessingMs = _meter.CreateHistogram<double>(
            "enterprise_api.orders.processing_duration",
            unit: "ms",
            description: "Time to process an order end-to-end");

        _ordersFailed = _meter.CreateCounter<long>(
            "enterprise_api.orders.failed",
            unit: "{orders}",
            description: "Total number of failed order attempts");

        _revenueTotal = _meter.CreateCounter<double>(
            "enterprise_api.revenue.total",
            unit: "USD",
            description: "Cumulative revenue from completed orders");

        _cacheHits = _meter.CreateCounter<long>(
            "enterprise_api.cache.hits",
            unit: "{requests}",
            description: "Cache hits across all layers");

        _cacheMisses = _meter.CreateCounter<long>(
            "enterprise_api.cache.misses",
            unit: "{requests}",
            description: "Cache misses — resulted in DB query");

        _dbQueryMs = _meter.CreateHistogram<double>(
            "enterprise_api.db.query_duration",
            unit: "ms",
            description: "Duration of database queries");

        _httpRequests = _meter.CreateCounter<long>(
            "enterprise_api.http.requests",
            unit: "{requests}",
            description: "Total HTTP requests by endpoint and status");

        _httpRequestMs = _meter.CreateHistogram<double>(
            "enterprise_api.http.request_duration",
            unit: "ms",
            description: "HTTP request duration by endpoint");

        _eventsPublished = _meter.CreateCounter<long>(
            "enterprise_api.kafka.events_published",
            unit: "{events}",
            description: "Total Kafka events successfully published");

        _eventsConsumed = _meter.CreateCounter<long>(
            "enterprise_api.kafka.events_consumed",
            unit: "{events}",
            description: "Total Kafka events consumed");

        _eventsFailedPublish = _meter.CreateCounter<long>(
            "enterprise_api.kafka.events_failed",
            unit: "{events}",
            description: "Kafka events that failed to publish");

        // Gauge reads current value on demand (when Prometheus scrapes)
        _dbConnectionsActive = _meter.CreateObservableGauge<int>(
            "enterprise_api.db.connections_active",
            observeValue: () => DbConnectionTracker.ActiveConnections,
            unit: "{connections}",
            description: "Currently active database connections");
    }

    // ── Public recording methods ─────────────────────────────────────────
    public void RecordOrderCreated(string status, decimal amount)
    {
        _ordersCreated.Add(1, new TagList { { "status", status } });
        _revenueTotal.Add((double)amount);
    }

    public void RecordOrderProcessingTime(double milliseconds, bool success)
        => _orderProcessingMs.Record(milliseconds,
            new TagList { { "success", success.ToString().ToLower() } });

    public void RecordOrderFailed(string reason)
        => _ordersFailed.Add(1, new TagList { { "reason", reason } });

    public void RecordCacheHit(string layer)
        => _cacheHits.Add(1, new TagList { { "layer", layer } });

    public void RecordCacheMiss()
        => _cacheMisses.Add(1);

    public void RecordDbQuery(double milliseconds, string operation)
        => _dbQueryMs.Record(milliseconds, new TagList { { "operation", operation } });

    public void RecordHttpRequest(string method, string endpoint, int statusCode, double ms)
    {
        var tags = new TagList
        {
            { "method", method },
            { "endpoint", endpoint },
            { "status_code", statusCode.ToString() }
        };
        _httpRequests.Add(1, tags);
        _httpRequestMs.Record(ms, tags);
    }

    public void RecordEventPublished(string topic)
        => _eventsPublished.Add(1, new TagList { { "topic", topic } });

    public void RecordEventConsumed(string topic)
        => _eventsConsumed.Add(1, new TagList { { "topic", topic } });

    public void RecordEventPublishFailed(string topic)
        => _eventsFailedPublish.Add(1, new TagList { { "topic", topic } });

    public void Dispose() => _meter.Dispose();
}

// ─── DB Connection Tracker ────────────────────────────────────────────────────
public static class DbConnectionTracker
{
    private static int _activeConnections = 0;
    public static int ActiveConnections => _activeConnections;
    public static void Increment() => Interlocked.Increment(ref _activeConnections);
    public static void Decrement() => Interlocked.Decrement(ref _activeConnections);
}

// ─── Request Telemetry Middleware ─────────────────────────────────────────────
// Records HTTP metrics and injects correlation IDs for distributed tracing.
// Every request gets a TraceId that flows through logs, Kafka events, and
// all downstream service calls — essential for debugging production incidents.
public sealed class TelemetryMiddleware
{
    private readonly RequestDelegate _next;
    private readonly EnterpriseApiMetrics _metrics;
    private readonly ILogger<TelemetryMiddleware> _logger;

    // ActivitySource enables distributed tracing via OpenTelemetry
    // The name matches what you configure in your OTel exporter
    private static readonly ActivitySource _activitySource =
        new("EnterpriseApi.HTTP");

    public TelemetryMiddleware(
        RequestDelegate next,
        EnterpriseApiMetrics metrics,
        ILogger<TelemetryMiddleware> logger)
    {
        _next = next;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Generate or propagate a correlation ID
        // If the caller sends X-Correlation-Id (e.g. from a frontend or API gateway),
        // preserve it so the whole request chain shares one ID.
        var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.NewGuid().ToString("N");

        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers["X-Correlation-Id"] = correlationId;

        // Start a tracing span for this request
        using var activity = _activitySource.StartActivity(
            $"{context.Request.Method} {context.Request.Path}",
            ActivityKind.Server);

        activity?.SetTag("http.method", context.Request.Method);
        activity?.SetTag("http.url", context.Request.Path);
        activity?.SetTag("correlation.id", correlationId);

        // Push correlation ID into log scope — it appears in every log line
        // written during this request without passing it around manually
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["RequestPath"] = context.Request.Path.ToString(),
            ["RequestMethod"] = context.Request.Method
        });

        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            var statusCode = context.Response.StatusCode;

            activity?.SetTag("http.status_code", statusCode);
            if (statusCode >= 500)
                activity?.SetStatus(ActivityStatusCode.Error);

            _metrics.RecordHttpRequest(
                context.Request.Method,
                // Normalise path — "/api/v1/products/some-specific-id" → "/api/v1/products/{id}"
                // Without this, every unique ID creates a new metric series (cardinality explosion)
                NormalisePath(context.Request.Path),
                statusCode,
                sw.Elapsed.TotalMilliseconds);

            // Log slow requests — threshold: 500ms
            // In production, anything > 200ms warrants investigation
            if (sw.Elapsed.TotalMilliseconds > 500)
            {
                _logger.LogWarning(
                    "Slow request: {Method} {Path} took {ElapsedMs}ms, status {StatusCode}",
                    context.Request.Method,
                    context.Request.Path,
                    (int)sw.Elapsed.TotalMilliseconds,
                    statusCode);
            }
        }
    }

    private static string NormalisePath(PathString path)
    {
        // Replace GUIDs and numeric IDs with placeholders
        // This prevents cardinality explosion in your metrics time series DB
        var normalized = path.ToString();
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            "{id}");
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized, @"/\d+", "/{id}");
        return normalized;
    }
}
