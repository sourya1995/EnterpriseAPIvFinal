// ═════════════════════════════════════════════════════════════════════════════
// UPDATED ServiceCollectionExtensions — wires all enhancements
//
// This is the single registration file that reflects the "billion user" upgrades.
// Add to your existing ServiceCollectionExtensions files.
// ═════════════════════════════════════════════════════════════════════════════

// FIX 1: Added missing using for Application layer interfaces.
// IProductService, IEventPublisher, ICacheService, IDistributedLock
// all live in EnterpriseApi.Application.Interfaces — without this using,
// every reference to those types is an unresolved symbol.
using EnterpriseApi.Application.Interfaces;
using EnterpriseApi.Infrastructure.Caching;
using EnterpriseApi.Infrastructure.Data;
using EnterpriseApi.Infrastructure.Observability;
using EnterpriseApi.Infrastructure.Resilience;
using EnterpriseApi.Infrastructure.TwelveFactors;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
// FIX 2: Removed unused `using Microsoft.Extensions.Options`.
// It was imported but nothing in this file uses IOptions<T> or IOptionsSnapshot<T>.
// Unused usings are warnings that clutter the build output.

namespace EnterpriseApi.Infrastructure.Extensions;

public static class EnhancedServiceCollectionExtensions
{
    public static IServiceCollection AddBillionUserCapabilities(
        this IServiceCollection services,
        IConfiguration config)
    {
        // ── Strongly-typed config ─────────────────────────────────────────
        services.Configure<JwtSettings>(config.GetSection("JwtSettings"));
        services.Configure<KafkaSettings>(config.GetSection("Kafka"));
        services.Configure<RedisSettings>(config.GetSection("Redis"));

        // FIX 3: Changed config section from "ConnectionStrings" to "Database".
        //
        // "ConnectionStrings" in appsettings.json is a flat key→value section:
        //   { "ConnectionStrings": { "DefaultConnection": "Data Source=..." } }
        //
        // services.Configure<DatabaseSettings>(config.GetSection("ConnectionStrings"))
        // would try to bind the whole ConnectionStrings object to DatabaseSettings,
        // meaning DatabaseSettings.DefaultConnection would look for a nested key
        // "ConnectionStrings:DefaultConnection:DefaultConnection" — wrong.
        //
        // The correct pattern is a dedicated "Database" section in appsettings.json:
        //   { "Database": { "DefaultConnection": "...", "CommandTimeoutSeconds": 30 } }
        //
        // Add this to appsettings.json and appsettings.Development.json:
        //   "Database": {
        //     "DefaultConnection": "Data Source=enterprise.db",
        //     "CommandTimeoutSeconds": 30,
        //     "MaxRetryCount": 3,
        //     "EnableSensitiveDataLogging": false
        //   }
        services.Configure<DatabaseSettings>(config.GetSection("Database"));

        var redisSettings = config.GetSection("Redis").Get<RedisSettings>()
            ?? new RedisSettings();

        // ── Caching ───────────────────────────────────────────────────────
        // L1: In-process memory cache (per-instance, microseconds)
        services.AddMemoryCache(options =>
        {
            // Cap at 200MB — prevents runaway memory growth
            options.SizeLimit = 200 * 1024 * 1024;
            options.TrackStatistics = true;
        });

        // L2: Distributed cache → Redis in production, in-memory in dev
        if (redisSettings.Enabled)
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisSettings.ConnectionString;
                options.InstanceName = $"{redisSettings.KeyPrefix}:cache:";
            });

            // Register the full Redis connection multiplexer.
            // Needed for pattern-based invalidation and distributed locks.
            services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
                StackExchange.Redis.ConnectionMultiplexer.Connect(
                    redisSettings.ConnectionString));

            services.AddSingleton<IDistributedLock, RedisDistributedLock>();
        }
        else
        {
            // Dev fallback: in-memory distributed cache (single-instance only)
            services.AddDistributedMemoryCache();

            // No-op lock for dev — always acquires, never coordinates
            services.AddSingleton<IDistributedLock, NoOpDistributedLock>();
        }

        // Register the two-level cache as the primary ICacheService
        services.AddSingleton<ICacheService, TwoLevelCacheService>();

        // Decorate IProductService with the caching wrapper.
        // Every call to IProductService now goes through CachedProductService first.
        // Requires Scrutor: dotnet add package Scrutor
        services.Decorate<IProductService, CachedProductService>();

        // ── Observability ─────────────────────────────────────────────────
        // Metrics: register as singleton — one Meter per process
        services.AddSingleton<EnterpriseApiMetrics>();

        // OpenTelemetry distributed tracing
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddSource("EnterpriseApi.*")
                .AddAspNetCoreInstrumentation(options =>
                {
                    // Don't trace health checks — they're noise
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments("/health");
                })
                .AddEntityFrameworkCoreInstrumentation(options =>
                {
                    options.SetDbStatementForText = false; // Don't log SQL values (PII risk)
                })
                .AddOtlpExporter()) // Send to Jaeger/X-Ray/Honeycomb via OTLP
            .WithMetrics(metrics => metrics
                .AddMeter("EnterpriseApi")
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddPrometheusExporter()); // Exposes /metrics endpoint for Prometheus scraping

        // ── Resilience ────────────────────────────────────────────────────
        services.AddResiliencePolicies();

        // Wrap KafkaEventPublisher with resilience + outbox fallback.
        // Decorate<T>() requires IEventPublisher to already be registered —
        // this must come AFTER AddMessaging() in the calling code.
        services.Decorate<IEventPublisher, ResilientEventPublisher>();

        // ── Health Checks ─────────────────────────────────────────────────
        var healthChecks = services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>(
                name: "database",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                tags: new[] { "db", "ready" });

        if (redisSettings.Enabled)
        {
            healthChecks.AddRedis(
                redisSettings.ConnectionString,
                name: "redis",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
                tags: new[] { "cache", "ready" });
        }

        // ── Rate Limiting ─────────────────────────────────────────────────
        services.AddApiRateLimiting();

        // ── Graceful Shutdown ─────────────────────────────────────────────
        services.AddGracefulShutdown();

        return services;
    }

    // Register in Program.cs middleware pipeline in this exact order.
    // FIX 4: Removed the ExceptionHandlingMiddleware reference from this method.
    //
    // ExceptionHandlingMiddleware lives in EnterpriseApi.API.Middleware — that's
    // the API project. This file lives in EnterpriseApi.Infrastructure.Extensions —
    // the Infrastructure project. Infrastructure must never reference the API project
    // (that would invert the dependency direction and create a circular reference).
    //
    // Instead, UseEnhancedMiddleware registers only infrastructure-owned middleware.
    // ExceptionHandlingMiddleware is registered separately in Program.cs BEFORE
    // calling UseEnhancedMiddleware(), as shown in the comment below.
    //
    // Correct Program.cs order:
    //   app.UseMiddleware<ExceptionHandlingMiddleware>(); // ← in Program.cs
    //   app.UseEnhancedMiddleware();                      // ← this method
    //   app.UseAuthentication();
    //   app.UseAuthorization();
    //   app.MapControllers();
    public static WebApplication UseEnhancedMiddleware(this WebApplication app)
    {
        // 1. Telemetry — captures timing for everything below
        app.UseMiddleware<TelemetryMiddleware>();

        // 2. Rate limiting — rejects excess requests before they reach auth
        app.UseRateLimiter();

        // 3. Prometheus metrics endpoint — scrape target for Grafana
        app.MapPrometheusScrapingEndpoint("/metrics")
            // Secure the metrics endpoint — only internal scrapers should see it
            .RequireHost("localhost", "*.internal");

        // 4. Health checks
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/health/ready",
            new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains("ready")
            });

        return app;
    }
}

// No-op lock for development without Redis
internal sealed class NoOpDistributedLock : IDistributedLock
{
    public Task<IAsyncDisposable?> TryAcquireAsync(
        string resource, TimeSpan ttl, CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable?>(new NoOpHandle());

    private sealed class NoOpHandle : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

// NuGet packages to add for these enhancements:
//
// dotnet add EnterpriseApi.Infrastructure package Microsoft.Extensions.Caching.StackExchangeRedis
// dotnet add EnterpriseApi.Infrastructure package StackExchange.Redis
// dotnet add EnterpriseApi.Infrastructure package Microsoft.Extensions.Http.Resilience
// dotnet add EnterpriseApi.Infrastructure package Polly.Extensions
// dotnet add EnterpriseApi.Infrastructure package OpenTelemetry.Extensions.Hosting
// dotnet add EnterpriseApi.Infrastructure package OpenTelemetry.Instrumentation.AspNetCore
// dotnet add EnterpriseApi.Infrastructure package OpenTelemetry.Instrumentation.EntityFrameworkCore
// dotnet add EnterpriseApi.Infrastructure package OpenTelemetry.Exporter.OpenTelemetryProtocol
// dotnet add EnterpriseApi.Infrastructure package OpenTelemetry.Exporter.Prometheus.AspNetCore
// dotnet add EnterpriseApi.Infrastructure package Scrutor  ← for .Decorate()
// dotnet add EnterpriseApi.API package AspNetCore.HealthChecks.Redis