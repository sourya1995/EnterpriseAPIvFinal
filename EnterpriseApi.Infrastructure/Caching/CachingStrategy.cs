// ═════════════════════════════════════════════════════════════════════════════
// MULTI-LAYER CACHING STRATEGY
//
// At 1B users, your database is not your bottleneck — your cache strategy is.
//
// Architecture:
//   L1 — IMemoryCache (in-process, microseconds, per-instance, ~100MB cap)
//   L2 — IDistributedCache → Redis (cross-instance, milliseconds, shared)
//   DB — SQL/SQLite (source of truth, only hit on L1+L2 miss)
//
// Pattern: Cache-Aside (Lazy Population)
//   Read:  Check L1 → Check L2 → Load from DB → populate L2 → populate L1
//   Write: Update DB → invalidate L2 → invalidate L1 (write-through for critical)
//
// Why not write-through everywhere?
//   Write-through keeps cache always consistent but doubles write latency.
//   For an order system, reads vastly outnumber writes (100:1 on product catalog).
//   Cache-aside lets hot reads be lightning fast at the cost of a brief stale window.
//   For price/inventory, use short TTLs (30s). For static data, use long TTLs (1h).
//
// What NOT to cache:
//   - User-specific order history (too granular, low reuse across users)
//   - Anything that must be perfectly consistent (payment state)
//   - Data smaller than the serialization overhead (tiny config values)
// ═════════════════════════════════════════════════════════════════════════════

// FIX: Added missing usings. Without these, IProductService, all DTOs (ProductDto,
// PagedResult, CreateProductRequest, etc.) and ConcurrentDictionary are unresolved.
using EnterpriseApi.Application.DTOs;
using EnterpriseApi.Application.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace EnterpriseApi.Infrastructure.Caching;

// ─── Cache Key Constants ──────────────────────────────────────────────────────
// Centralising keys prevents typos and makes cache invalidation discoverable.
// Every key includes a version prefix — bump it to instantly invalidate all
// cached entries of that type without touching Redis directly.
public static class CacheKeys
{
    // Product catalog — changes infrequently, read by everyone
    // Version prefix: bump "v1" to "v2" to invalidate ALL product cache entries
    public static string Product(Guid id)         => $"v1:product:{id}";
    public static string ProductList(int page)    => $"v1:products:page:{page}";
    public static string ProductBySku(string sku) => $"v1:product:sku:{sku}";

    // Inventory — changes frequently, but eventual consistency is acceptable
    // for display purposes. Actual reservation uses DB with pessimistic locking.
    public static string Inventory(Guid productId) => $"v1:inventory:{productId}";

    // User profile — read on every authenticated request (authorization checks)
    public static string UserProfile(Guid userId) => $"v1:user:profile:{userId}";
    public static string UserRoles(Guid userId)   => $"v1:user:roles:{userId}";

    // Order summaries — read by managers, not per-user hot path
    public static string OrderSummary(Guid orderId) => $"v1:order:summary:{orderId}";

    // Global stats — expensive aggregation queries, refresh every 5 minutes
    public static string DashboardStats() => "v1:stats:dashboard";

    // Rate limiting counters — use Redis directly, not this cache layer
    // (These are here for documentation purposes — see RateLimiting.cs)
}

// ─── Cache TTL Configuration ──────────────────────────────────────────────────
public static class CacheTtl
{
    // Product catalog: 10 minutes. Stale product name for 10 min is acceptable.
    // Stale price for 10 min is NOT acceptable — use 30 seconds for prices.
    public static readonly TimeSpan ProductCatalog  = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ProductPrice    = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan InventoryLevel  = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan UserProfile     = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DashboardStats  = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan OrderSummary    = TimeSpan.FromMinutes(2);

    // L1 (in-memory) TTL is always shorter than L2 (Redis) TTL.
    // This ensures L1 entries expire before L2, so stale L1 data
    // refreshes from Redis rather than going all the way to the DB.
    public static TimeSpan L1FromL2(TimeSpan l2Ttl) =>
        TimeSpan.FromTicks(l2Ttl.Ticks / 2);
}

// ─── Two-Level Cache Interface ────────────────────────────────────────────────
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class;

    Task SetAsync<T>(string key, T value, TimeSpan ttl,
        CancellationToken ct = default) where T : class;

    Task RemoveAsync(string key, CancellationToken ct = default);

    // Remove all keys matching a pattern — used for bulk invalidation.
    // e.g. RemoveByPatternAsync("v1:product:*") clears entire product cache.
    // WARNING: SCAN-based, expensive on large Redis keyspaces. Use sparingly.
    Task RemoveByPatternAsync(string pattern, CancellationToken ct = default);

    // The most important method: get-or-set with a factory.
    // Handles stampede protection via SemaphoreSlim per key.
    Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory,
        TimeSpan ttl, CancellationToken ct = default) where T : class;
}

// ─── Two-Level Cache Implementation ──────────────────────────────────────────
public sealed class TwoLevelCacheService : ICacheService
{
    private readonly IMemoryCache _l1;
    private readonly IDistributedCache _l2;
    private readonly ILogger<TwoLevelCacheService> _logger;

    // Per-key semaphores prevent cache stampedes:
    // When a popular key expires, thousands of requests hit the DB simultaneously.
    // The semaphore ensures only ONE request populates the cache.
    // All others wait and then read from the freshly populated cache.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    // JSON options: use camelCase for smaller serialized size
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public TwoLevelCacheService(
        IMemoryCache l1,
        IDistributedCache l2,
        ILogger<TwoLevelCacheService> logger)
    {
        _l1 = l1;
        _l2 = l2;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        where T : class
    {
        // L1 hit — fastest path, no network call
        if (_l1.TryGetValue(key, out T? l1Value))
        {
            _logger.LogDebug("L1 cache hit for key {Key}", key);
            return l1Value;
        }

        // L2 hit — one Redis round trip (~0.5ms within same DC)
        try
        {
            var bytes = await _l2.GetAsync(key, ct);
            if (bytes is not null)
            {
                var value = JsonSerializer.Deserialize<T>(bytes, _jsonOptions);
                if (value is not null)
                {
                    // Backfill L1 — next request won't need the Redis round trip
                    _l1.Set(key, value, TimeSpan.FromMinutes(1));
                    _logger.LogDebug("L2 cache hit for key {Key}", key);
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            // Redis is DOWN — don't crash the application.
            // Log, record metric, and fall through to the database.
            // This is the single most important resilience decision in this file.
            _logger.LogWarning(ex, "L2 cache unavailable for key {Key}, falling through to source", key);
        }

        _logger.LogDebug("Cache miss for key {Key}", key);
        return null;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl,
        CancellationToken ct = default) where T : class
    {
        // Set L1 with half the TTL — ensures L1 expires before L2
        // so stale L1 data refreshes from Redis, not DB
        _l1.Set(key, value, CacheTtl.L1FromL2(ttl));

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions);
            await _l2.SetAsync(key, bytes, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl
            }, ct);
        }
        catch (Exception ex)
        {
            // Redis write failure should NOT fail the request.
            // The data was already returned to the caller from the DB.
            // We just lose the caching benefit for this one request.
            _logger.LogWarning(ex, "Failed to write to L2 cache for key {Key}", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _l1.Remove(key);

        try
        {
            await _l2.RemoveAsync(key, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove key {Key} from L2 cache", key);
        }
    }

    public async Task RemoveByPatternAsync(string pattern, CancellationToken ct = default)
    {
        // Pattern-based deletion requires direct Redis connection, not IDistributedCache.
        // IDistributedCache is an abstraction that doesn't expose SCAN.
        // This method is intentionally left as a log+no-op for the abstracted interface.
        // In production, inject IConnectionMultiplexer (StackExchange.Redis) directly
        // alongside IDistributedCache for pattern operations.
        _logger.LogWarning(
            "RemoveByPatternAsync called with pattern {Pattern}. " +
            "For full pattern support, inject IConnectionMultiplexer directly.", pattern);
        await Task.CompletedTask;
    }

    public async Task<T> GetOrSetAsync<T>(string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan ttl,
        CancellationToken ct = default) where T : class
    {
        // Fast path: already cached
        var cached = await GetAsync<T>(key, ct);
        if (cached is not null)
            return cached;

        // Stampede protection: only one concurrent caller populates the cache
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock —
            // another thread may have populated it while we were waiting
            cached = await GetAsync<T>(key, ct);
            if (cached is not null)
                return cached;

            // Cache miss confirmed under lock — call the factory (DB query)
            var value = await factory(ct);
            await SetAsync(key, value, ttl, ct);
            return value;
        }
        finally
        {
            semaphore.Release();
            // Clean up semaphore if no other threads are waiting
            if (semaphore.CurrentCount == 1)
                _locks.TryRemove(key, out _);
        }
    }
}

// ─── Cached Product Service (demonstrates the pattern) ───────────────────────
// This wraps the existing ProductService with caching.
// Uses the Decorator pattern — no changes to ProductService itself.
// Register this as the primary IProductService in DI, wrapping the real one.
//
// FIX: Replaced the entire class body. The original had two bugs:
//
//   Bug A — Wrong method signatures: the original had GetBySkuAsync() and
//   GetPagedAsync() which do NOT exist on IProductService. The real interface
//   has GetAllAsync(int page, int pageSize, string? category, ct) and
//   no GetBySkuAsync. A class claiming to implement an interface must implement
//   every method with the exact signature — this was a compile error.
//
//   Bug B — Nullable mismatch on GetByIdAsync: the original returned
//   Task<ProductDto?> (nullable) but the interface declares Task<ProductDto>
//   (non-nullable). The compiler treats these as different return types.
public sealed class CachedProductService : IProductService
{
    private readonly IProductService _inner;
    private readonly ICacheService _cache;
    private readonly ILogger<CachedProductService> _logger;

    public CachedProductService(
        IProductService inner,
        ICacheService cache,
        ILogger<CachedProductService> logger)
    {
        _inner = inner;
        _cache = cache;
        _logger = logger;
    }

    // Return type matches interface exactly: Task<ProductDto> (non-nullable).
    // The interface contract guarantees the real service throws NotFoundException
    // instead of returning null, so the cache layer can safely return non-null.
    public async Task<ProductDto> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _cache.GetOrSetAsync(
            CacheKeys.Product(id),
            _ => _inner.GetByIdAsync(id, ct),
            CacheTtl.ProductCatalog,
            ct);
    }

    // FIX: Replaced GetBySkuAsync (not on IProductService) with the correct
    // GetAllAsync(page, pageSize, category, ct) matching the real interface.
    // Only the first 5 pages are cached — page 847 is read by nobody and
    // wastes Redis memory. Requests beyond page 5 go straight to the DB.
    public async Task<PagedResult<ProductDto>> GetAllAsync(
        int page, int pageSize, string? category = null, CancellationToken ct = default)
    {
        // Don't cache category-filtered requests or pages beyond 5 —
        // too many permutations, low reuse, not worth the Redis memory.
        if (page <= 5 && category is null)
        {
            return await _cache.GetOrSetAsync(
                CacheKeys.ProductList(page),
                _ => _inner.GetAllAsync(page, pageSize, null, ct),
                CacheTtl.ProductCatalog,
                ct);
        }

        return await _inner.GetAllAsync(page, pageSize, category, ct);
    }

    public async Task<ProductDto> CreateAsync(
        CreateProductRequest request, CancellationToken ct = default)
    {
        var product = await _inner.CreateAsync(request, ct);
        // New product — no existing cache entry to invalidate
        return product;
    }

    public async Task<ProductDto> UpdateAsync(
        Guid id, UpdateProductRequest request, CancellationToken ct = default)
    {
        var product = await _inner.UpdateAsync(id, request, ct);

        // Invalidate the single-item key and the first 5 paginated list pages.
        // We don't know which page this product is on, so we invalidate all of them.
        // In a high-write system, use event-driven cache invalidation via Kafka instead.
        await _cache.RemoveAsync(CacheKeys.Product(id));
        for (var i = 1; i <= 5; i++)
            await _cache.RemoveAsync(CacheKeys.ProductList(i));

        _logger.LogDebug("Invalidated cache for product {ProductId}", id);

        return product;
    }

    public async Task<ProductDto> ApplyDiscountAsync(
        Guid id, ApplyDiscountRequest request, CancellationToken ct = default)
    {
        var product = await _inner.ApplyDiscountAsync(id, request, ct);

        // Price changed — must invalidate immediately. Stale price = wrong charge.
        await _cache.RemoveAsync(CacheKeys.Product(id));
        for (var i = 1; i <= 5; i++)
            await _cache.RemoveAsync(CacheKeys.ProductList(i));

        return product;
    }

    public async Task<ProductDto> RestockAsync(
        Guid id, RestockRequest request, CancellationToken ct = default)
    {
        // Inventory changed — invalidate the product entry so the new stock
        // level is visible on the next read.
        var product = await _inner.RestockAsync(id, request, ct);
        await _cache.RemoveAsync(CacheKeys.Product(id));
        return product;
    }

    public async Task DeactivateAsync(Guid id, CancellationToken ct = default)
    {
        await _inner.DeactivateAsync(id, ct);

        // Deactivated product must not appear in cached results.
        await _cache.RemoveAsync(CacheKeys.Product(id));
        for (var i = 1; i <= 5; i++)
            await _cache.RemoveAsync(CacheKeys.ProductList(i));
    }
}