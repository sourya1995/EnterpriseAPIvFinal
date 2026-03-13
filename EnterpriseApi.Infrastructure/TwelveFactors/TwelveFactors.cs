// ═════════════════════════════════════════════════════════════════════════════
// THE TWELVE-FACTOR APP — APPLIED TO ENTERPRISEAPI
//
// The 12 factors were written by Heroku engineers in 2011 based on observing
// thousands of apps. They describe the difference between apps that scale
// cleanly to millions of users and apps that collapse under their own weight.
//
// Below is each factor, what it means, why it matters at scale, and exactly
// what to change in this project to comply with it.
// ═════════════════════════════════════════════════════════════════════════════

using EnterpriseApi.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EnterpriseApi.Infrastructure.TwelveFactors;

// ─── Factor I: Codebase ───────────────────────────────────────────────────────
// "One codebase tracked in revision control, many deploys."
//
// ✅ Already done: Single git repo, multiple environments (staging, production)
//    are different deploys of the same code — not different branches with
//    environment-specific code merged into them.
//
// ❌ Anti-pattern to avoid: having an `if (environment == "prod")` branch that
//    contains production-only business logic. Env differences belong in config only.

// ─── Factor II: Dependencies ──────────────────────────────────────────────────
// "Explicitly declare and isolate dependencies."
//
// ✅ Already done: All dependencies are in .csproj files. dotnet restore
//    downloads them. No dependency on anything installed on the host machine.
//
// The .csproj pattern for pinning exact versions:
// <PackageReference Include="Confluent.Kafka" Version="2.4.0" />
//                                                       ^^^^^
//                                          Pinned — not "2.*" or "latest"
//
// Why pin? "2.*" could pull 2.5.0 tomorrow with a breaking change.
// You want reproducible builds. Same code + same packages = same binary every time.

// ─── Factor III: Config ───────────────────────────────────────────────────────
// "Store config in the environment."
//
// Config is everything that varies between deploys (staging vs production).
// It must NEVER be committed to the repo.
//
// ❌ Bad: hardcoded connection strings, JWT secrets in appsettings.json in git
// ✅ Good: appsettings.json in git contains only structure, not values.
//          Real values come from environment variables or AWS Secrets Manager.

public static class ConfigurationSetup
{
    // This is what your Program.cs should use for configuration loading.
    // Priority order (highest wins): EnvVars > appsettings.{Environment}.json > appsettings.json
    // This means you can override ANY setting with an environment variable in ECS.
    public static IConfigurationBuilder AddTwelveFactorConfig(
        this IConfigurationBuilder config, string environment)
    {
        return config
            // Base config — structure only, safe to commit
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            // Environment overrides — no secrets, safe to commit
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            // Environment variables — HIGHEST priority, set in ECS task definition
            // Convention: ConnectionStrings__DefaultConnection maps to ConnectionStrings:DefaultConnection
            .AddEnvironmentVariables()
            // For local development only — gitignored file with real secrets
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
    }
}

// Strongly-typed configuration — no magic strings anywhere in the application
// Register with: services.Configure<JwtSettings>(config.GetSection("JwtSettings"))
// Use with: IOptions<JwtSettings> or IOptionsSnapshot<JwtSettings>
public record JwtSettings
{
    public required string Issuer    { get; init; }
    public required string Audience  { get; init; }
    public required string Secret    { get; init; }
    public int ExpiryMinutes         { get; init; } = 60;
}

public record DatabaseSettings
{
    public required string DefaultConnection { get; init; }
    public int CommandTimeoutSeconds         { get; init; } = 30;
    public int MaxRetryCount                 { get; init; } = 3;
    public bool EnableSensitiveDataLogging   { get; init; } = false; // NEVER true in production
}

public record KafkaSettings
{
    public bool   Enabled           { get; init; } = false;
    public string BootstrapServers  { get; init; } = "localhost:9092";
    public string ConsumerGroupId   { get; init; } = "enterprise-api";
    public int    ProducerLingerMs  { get; init; } = 5;
}

public record RedisSettings
{
    public bool   Enabled           { get; init; } = false;
    public string ConnectionString  { get; init; } = "localhost:6379";
    public int    DefaultTtlMinutes { get; init; } = 10;
    public string KeyPrefix         { get; init; } = "enterprise";
}

// ─── Factor IV: Backing Services ─────────────────────────────────────────────
// "Treat backing services as attached resources."
//
// Database, Redis, Kafka — all are "attached resources" with a URL.
// Swapping from SQLite (dev) to PostgreSQL (prod) should be a config change,
// not a code change. Your code should speak to an interface, not a concrete driver.
//
// ✅ Already done: IUnitOfWork, IEventPublisher, ICacheService are all interfaces.
//    The Infrastructure layer provides concrete implementations.
//    To swap from SQLite to PostgreSQL: change one line in ServiceCollectionExtensions.
//
// ─── Factor V: Build, Release, Run ───────────────────────────────────────────
// "Strictly separate build and run stages."
//
// Build:   dotnet publish → immutable artifact (a Docker image with a SHA tag)
// Release: artifact + config (image:sha256 + ECS task definition environment vars)
// Run:     ECS starts the container — no code changes possible at runtime
//
// ✅ Already done: The CI/CD pipeline builds an immutable image tagged with
//    branch-shortsha-timestamp. Once built, it never changes. Deploy = config swap.
//
// ─── Factor VI: Processes ────────────────────────────────────────────────────
// "Execute the app as one or more stateless processes."
//
// The most important factor for horizontal scaling.
// EVERY request must be completable by ANY instance of your app.
// NO sticky sessions. NO in-process state shared across requests.
//
// ❌ Bad patterns:
//   - Storing session in MemoryCache (dies when the container restarts)
//   - Using a static Dictionary to track "active users" (invisible to other pods)
//   - Background job state in a static variable
//
// ✅ Good patterns:
//   - Sessions in Redis (shared across all instances)
//   - "Active users" as a Redis sorted set
//   - Background job coordination via DB or Redis

// This is what your distributed session should look like:
public static class SessionConfiguration
{
    public static IServiceCollection AddDistributedSession(
        this IServiceCollection services,
        RedisSettings redis)
    {
        if (redis.Enabled)
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redis.ConnectionString;
                options.InstanceName = $"{redis.KeyPrefix}:session:";
            });
        }
        else
        {
            // Dev fallback — in-process, not suitable for production
            services.AddDistributedMemoryCache();
        }

        services.AddSession(options =>
        {
            options.IdleTimeout = TimeSpan.FromMinutes(30);
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
            options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
        });

        return services;
    }
}

// ─── Factor VII: Port Binding ─────────────────────────────────────────────────
// "Export services via port binding."
//
// Your app is self-contained. It does not rely on Nginx being installed on the host.
// Kestrel IS the web server. The port is injected via config.
// The Dockerfile sets ASPNETCORE_URLS=http://+:8080 — this is Factor VII in action.
//
// ─── Factor VIII: Concurrency ────────────────────────────────────────────────
// "Scale out via the process model."
//
// Need more throughput? Add more instances. Not more cores per instance.
// Horizontal scaling is cheaper, more resilient, and simpler than vertical.
//
// ✅ Already done: Stateless app + ECS auto-scaling. When CPU > 70%, ECS adds tasks.
//    Each task is an independent process — no shared in-process state.
//
// The right mental model: 1 Fargate task = 1 process. 10 tasks = 10 processes.
// No coordinator, no election, no distributed locking needed for HTTP serving.
// (You DO need distributed locking for specific operations — see below.)

// Distributed lock for operations that must run on exactly one instance
// Example: scheduled job that must not run concurrently across pods
public interface IDistributedLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(
        string resource, TimeSpan ttl, CancellationToken ct = default);
}

// Redis implementation — acquire is an atomic SET NX EX operation
public sealed class RedisDistributedLock : IDistributedLock
{
    private readonly StackExchange.Redis.IConnectionMultiplexer _redis;
    private readonly ILogger<RedisDistributedLock> _logger;

    public RedisDistributedLock(
        StackExchange.Redis.IConnectionMultiplexer redis,
        ILogger<RedisDistributedLock> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string resource, TimeSpan ttl, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var lockKey = $"lock:{resource}";
        var lockValue = Guid.NewGuid().ToString("N");

        var acquired = await db.StringSetAsync(
            lockKey, lockValue, ttl, StackExchange.Redis.When.NotExists);

        if (!acquired)
        {
            _logger.LogDebug("Could not acquire distributed lock for {Resource}", resource);
            return null;
        }

        return new RedisLockHandle(db, lockKey, lockValue, _logger);
    }

    private sealed class RedisLockHandle : IAsyncDisposable
    {
        private readonly StackExchange.Redis.IDatabase _db;
        private readonly string _key;
        private readonly string _value;
        private readonly ILogger _logger;

        public RedisLockHandle(StackExchange.Redis.IDatabase db, string key,
            string value, ILogger logger)
        { _db = db; _key = key; _value = value; _logger = logger; }

        public async ValueTask DisposeAsync()
        {
            // Use a Lua script to atomically check-and-delete
            // This prevents releasing a lock owned by another instance
            const string script = """
                if redis.call("GET", KEYS[1]) == ARGV[1] then
                    return redis.call("DEL", KEYS[1])
                else
                    return 0
                end
                """;

            await _db.ScriptEvaluateAsync(script,
                new StackExchange.Redis.RedisKey[] { _key },
                new StackExchange.Redis.RedisValue[] { _value });
        }
    }
}

// ─── Factor IX: Disposability ─────────────────────────────────────────────────
// "Maximise robustness with fast startup and graceful shutdown."
//
// Fast startup: your app must be ready to serve in < 10 seconds.
//   ECS waits for your health check to pass. Slow startup = slow deploys.
//   ✅ SQLite creates the DB on first health check, not on startup.
//
// Graceful shutdown: when ECS sends SIGTERM (before killing the container),
//   finish in-flight requests and commit in-flight Kafka messages.

public static class GracefulShutdownExtensions
{
    public static IServiceCollection AddGracefulShutdown(this IServiceCollection services)
    {
        // Give the app 30 seconds to finish in-flight requests after SIGTERM
        // ECS waits up to 30 seconds (deregistration delay) before SIGKILL
        services.Configure<Microsoft.AspNetCore.HostFilteringMiddleware>(options => { });

        services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
        {
            // Don't accept new connections after shutdown begins
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(25);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });

        return services;
    }
}

// ─── Factor X: Dev/Prod Parity ───────────────────────────────────────────────
// "Keep development, staging, and production as similar as possible."
//
// The biggest risk: you develop against SQLite, ship to PostgreSQL,
// discover at 2AM that a query that works in SQLite fails on PostgreSQL.
//
// Ideal state:
//   Dev       → Docker Compose with PostgreSQL, Redis, Kafka (real services)
//   Staging   → Same infrastructure as production, smaller sizing
//   Production → Full scale
//
// ✅ The docker-compose.yml already provides Kafka for local dev.
//    Add Redis and PostgreSQL to it for full parity.

// ─── Factor XI: Logs ─────────────────────────────────────────────────────────
// "Treat logs as event streams."
//
// Your app should never open log files. It writes to stdout only.
// The infrastructure (CloudWatch, Elasticsearch, Datadog) collects and stores them.
// This is already handled by the TelemetryMiddleware + structured logging.
//
// The key configuration — write structured JSON to stdout, nothing else:
public static class LoggingConfiguration
{
    public static ILoggingBuilder AddTwelveFactorLogging(this ILoggingBuilder logging)
    {
        logging.ClearProviders();

        // Console only — the container runtime captures stdout and sends it
        // to CloudWatch/Datadog/whatever your infrastructure uses
        logging.AddConsole(options =>
        {
            // Structured JSON format — every field is queryable in CloudWatch Insights
            // "SELECT * FROM logs WHERE correlationId = 'abc'" works with structured logs.
            // It does NOT work with string logs.
            options.FormatterName = "json";
        });

        // In development, use human-readable format instead
        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development")
        {
            logging.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = false;
                options.TimestampFormat = "HH:mm:ss ";
            });
        }

        return logging;
    }
}

// ─── Factor XII: Admin Processes ─────────────────────────────────────────────
// "Run admin/management tasks as one-off processes."
//
// Database migrations, data backfills, one-time scripts —
// these run as ephemeral tasks, not as part of the running app.
// They use the SAME codebase and SAME config as production.
//
// ❌ Bad: "SSH into the prod server and run this SQL script"
// ✅ Good: ECS one-off task that runs the migration, then exits
//
// In ECS: "Run Task" with the same image, override the entrypoint to
// run migrations instead of starting the API.
// This ensures migrations run with production credentials,
// the same .NET version, and are tracked in your audit log.

// Run with: dotnet run --migrate-only
// In ECS: override command to ["dotnet", "EnterpriseApi.API.dll", "--migrate-only"]
public static class AdminProcesses
{
    public static async Task RunMigrationsIfRequestedAsync(WebApplication app)
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--migrate-only"))
            return;

        // FIX A — Cross-layer ILogger<Program> reference removed.
        //
        // Original: app.Services.GetRequiredService<ILogger<Program>>()
        //
        // `Program` is defined in EnterpriseApi.API. This file lives in
        // EnterpriseApi.Infrastructure. Infrastructure referencing the API project
        // creates a circular dependency (API already references Infrastructure).
        // The fix is to use ILogger<AdminProcesses> — a type defined right here
        // in Infrastructure — which is always available without any cross-project reference.
        var logger = app.Services.GetRequiredService<ILogger<AdminProcesses>>();
        logger.LogInformation("Running database migrations as one-off admin process");

        using var scope = app.Services.CreateScope();

        // FIX B — Wrong DbContext type.
        //
        // Original: GetRequiredService<Microsoft.EntityFrameworkCore.DbContext>()
        //
        // `DbContext` (the base class) is never directly registered in the DI container.
        // What IS registered is `AppDbContext` (the concrete class) via:
        //   services.AddDbContext<AppDbContext>(...)
        // Requesting the base class at runtime throws InvalidOperationException:
        //   "No service for type 'Microsoft.EntityFrameworkCore.DbContext' has been registered."
        // The fix is to resolve the concrete registered type AppDbContext directly.
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.MigrateAsync();
        logger.LogInformation("Migrations complete. Exiting.");

        // Explicitly exit — this is a one-off task, not a long-running service
        Environment.Exit(0);
    }
}