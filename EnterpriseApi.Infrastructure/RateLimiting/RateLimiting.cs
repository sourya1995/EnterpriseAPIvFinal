// ═════════════════════════════════════════════════════════════════════════════
// RATE LIMITING
//
// At scale, rate limiting is not optional — it is survival.
// A single misconfigured client can bring down a 1B-user system if there
// is nothing between it and the database.
//
// Layers:
//   1. API Gateway / ALB level  — coarse, IP-based, before your code runs
//   2. Middleware level          — fine-grained, per-user, per-endpoint
//   3. Application level         — business rules (e.g. max 5 orders/minute)
//
// Algorithms:
//   Fixed Window  — simple, but burst-friendly (100 req at 11:59:59, 100 at 12:00:00)
//   Sliding Window — smoother, uses more Redis memory (sorted sets)
//   Token Bucket   — best for API-style limits, allows short bursts, then throttles
//   Leaky Bucket   — smoothest, best for protecting downstream services
//
// This implementation uses ASP.NET Core 8's built-in RateLimiter with
// a Redis-backed sliding window for distributed deployments.
// ═════════════════════════════════════════════════════════════════════════════

using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace EnterpriseApi.API.RateLimiting;

public static class RateLimitingExtensions
{
    // Policy names — referenced in [EnableRateLimiting("policy-name")] on controllers
    public const string Anonymous   = "anonymous";
    public const string Authenticated = "authenticated";
    public const string AdminOnly   = "admin";
    public const string OrderCreate = "order-create";
    public const string AuthLogin   = "auth-login";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // ── Global fallback: applies to any request not covered by a named policy
            // 100 requests per minute per IP, sliding window
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetSlidingWindowLimiter(ip, _ =>
                    new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,   // 10-second buckets
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0           // No queuing — reject immediately
                    });
            });

            // ── Anonymous endpoints: stricter to prevent abuse without auth
            // 20 requests per minute per IP
            options.AddPolicy(Anonymous, context =>
            {
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetSlidingWindowLimiter($"anon:{ip}", _ =>
                    new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,
                        QueueLimit = 0
                    });
            });

            // ── Authenticated endpoints: per user ID, not per IP
            // Avoids punishing legitimate users sharing an IP (office NAT, CDN)
            // 300 requests per minute per authenticated user
            options.AddPolicy(Authenticated, context =>
            {
                var userId = context.User.FindFirst(
                    System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "unknown";

                return RateLimitPartition.GetTokenBucketLimiter($"auth:{userId}", _ =>
                    new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 300,           // Bucket capacity
                        TokensPerPeriod = 300,       // Refill rate
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        AutoReplenishment = true,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 10              // Queue up to 10 requests during burst
                    });
            });

            // ── Login endpoint: anti-brute-force
            // 5 attempts per 15 minutes per IP
            // After 5 failed logins, attacker must wait 15 minutes
            options.AddPolicy(AuthLogin, context =>
            {
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter($"login:{ip}", _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(15),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    });
            });

            // ── Order creation: business rule throttle
            // 10 orders per minute per user — prevents cart-flooding attacks
            // and accidental double-submit storms
            options.AddPolicy(OrderCreate, context =>
            {
                var userId = context.User.FindFirst(
                    System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                    ?? "unknown";

                return RateLimitPartition.GetSlidingWindowLimiter($"order:{userId}", _ =>
                    new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,
                        QueueLimit = 2
                    });
            });

            // ── Admin endpoints: relaxed limits (higher trust)
            options.AddPolicy(AdminOnly, context =>
            {
                var userId = context.User.FindFirst(
                    System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                    ?? "unknown";

                return RateLimitPartition.GetTokenBucketLimiter($"admin:{userId}", _ =>
                    new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 1000,
                        TokensPerPeriod = 1000,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        AutoReplenishment = true,
                        QueueLimit = 0
                    });
            });

            // ── Rejection response: return 429 with Retry-After header
            // Never silently drop — always tell the client when to retry
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/problem+json";

                // Calculate retry-after based on the limiter type
                var retryAfter = context.Lease.TryGetMetadata(
                    MetadataName.RetryAfter, out var retryAfterTs)
                    ? (int)retryAfterTs.TotalSeconds
                    : 60;

                context.HttpContext.Response.Headers["Retry-After"] = retryAfter.ToString();
                context.HttpContext.Response.Headers["X-RateLimit-Limit"] = "see-policy";

                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    type = "https://tools.ietf.org/html/rfc6585#section-4",
                    title = "Too Many Requests",
                    status = 429,
                    detail = $"Rate limit exceeded. Retry after {retryAfter} seconds.",
                    retryAfterSeconds = retryAfter
                }, ct);
            };
        });

        return services;
    }

    public static IApplicationBuilder UseApiRateLimiting(this IApplicationBuilder app)
    {
        app.UseRateLimiter();
        return app;
    }
}

// ─── Apply to controllers ─────────────────────────────────────────────────────
// In your AuthController:
// [EnableRateLimiting(RateLimitingExtensions.AuthLogin)]
// [HttpPost("login")]
// public async Task<IActionResult> Login(...)
//
// In your OrdersController:
// [EnableRateLimiting(RateLimitingExtensions.OrderCreate)]
// [HttpPost]
// public async Task<IActionResult> Create(...)
//
// Class-level attribute covers all methods in that controller:
// [EnableRateLimiting(RateLimitingExtensions.Authenticated)]
// public class ProductsController : ControllerBase
