using EnterpriseApi.Application.DTOs;
using EnterpriseApi.Application.Interfaces;
using EnterpriseApi.Domain.Entities;
using EnterpriseApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseApi.Infrastructure.Repositories;

// ─────────────────────────────────────────────────────────────────────────────
// USER REPOSITORY
// ─────────────────────────────────────────────────────────────────────────────
public class UserRepository : IUserRepository
{
    private readonly AppDbContext _ctx;
    public UserRepository(AppDbContext ctx) => _ctx = ctx;

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _ctx.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
        // Note: NOT AsNoTracking — auth service may need to update PasswordHash (rehash)
        => await _ctx.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

    public async Task<PagedResult<User>> GetAllAsync(int page, int pageSize,
        CancellationToken ct = default)
    {
        var query = _ctx.Users.AsNoTracking();
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(u => u.CreatedAt)   // Deterministic order — required for stable pagination
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<User>(items, total, page, pageSize,
            (int)Math.Ceiling((double)total / pageSize));
    }

    public async Task<User> AddAsync(User user, CancellationToken ct = default)
    {
        await _ctx.Users.AddAsync(user, ct);
        return user;
    }

    public async Task UpdateAsync(User user, CancellationToken ct = default)
    {
        _ctx.Users.Update(user);
        await Task.CompletedTask;
    }

    public async Task<bool> ExistsAsync(string email, CancellationToken ct = default)
        => await _ctx.Users.AsNoTracking()
            .AnyAsync(u => u.Email == email.ToLowerInvariant(), ct);
}

// ─────────────────────────────────────────────────────────────────────────────
// PRODUCT REPOSITORY
// ─────────────────────────────────────────────────────────────────────────────
public class ProductRepository : IProductRepository
{
    private readonly AppDbContext _ctx;
    public ProductRepository(AppDbContext ctx) => _ctx = ctx;

    public async Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _ctx.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<Product?> GetByIdWithInventoryAsync(Guid id, CancellationToken ct = default)
        // Include inventory for display purposes — AsNoTracking for read
        => await _ctx.Products.AsNoTracking()
            .Include(p => p.Inventory)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<Product?> GetBySkuAsync(string sku, CancellationToken ct = default)
        => await _ctx.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.SKU == sku.ToUpperInvariant(), ct);

    public async Task<PagedResult<Product>> GetAllAsync(int page, int pageSize,
        string? category = null, CancellationToken ct = default)
    {
        var query = _ctx.Products.AsNoTracking()
            .Include(p => p.Inventory)
            .Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(p => p.Category == category);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(p => p.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<Product>(items, total, page, pageSize,
            (int)Math.Ceiling((double)total / pageSize));
    }

    public async Task<Product> AddAsync(Product product, CancellationToken ct = default)
    {
        await _ctx.Products.AddAsync(product, ct);
        return product;
    }

    public async Task UpdateAsync(Product product, CancellationToken ct = default)
    {
        _ctx.Products.Update(product);
        await Task.CompletedTask;
    }

    public async Task<IEnumerable<Product>> GetLowStockProductsAsync(CancellationToken ct = default)
        => await _ctx.Products.AsNoTracking()
            .Include(p => p.Inventory)
            .Where(p => p.IsActive && p.Inventory != null &&
                        p.Inventory.AvailableStock <= p.Inventory.LowStockThreshold)
            .ToListAsync(ct);
}

// ─────────────────────────────────────────────────────────────────────────────
// INVENTORY REPOSITORY
// ─────────────────────────────────────────────────────────────────────────────
public class InventoryRepository : IInventoryRepository
{
    private readonly AppDbContext _ctx;
    public InventoryRepository(AppDbContext ctx) => _ctx = ctx;

    public async Task<Inventory?> GetByProductIdAsync(Guid productId, CancellationToken ct = default)
        => await _ctx.Inventories.AsNoTracking()
            .FirstOrDefaultAsync(i => i.ProductId == productId, ct);

    // Tracked version — used when we intend to update the inventory (order create/cancel/complete)
    // CRITICAL: Do NOT use AsNoTracking here — the EF change tracker must see the entity
    // to detect changes and generate UPDATE SQL.
    public async Task<Inventory?> GetByProductIdForUpdateAsync(Guid productId,
        CancellationToken ct = default)
        => await _ctx.Inventories.FirstOrDefaultAsync(i => i.ProductId == productId, ct);

    public async Task<Inventory> AddAsync(Inventory inventory, CancellationToken ct = default)
    {
        await _ctx.Inventories.AddAsync(inventory, ct);
        return inventory;
    }

    public async Task UpdateAsync(Inventory inventory, CancellationToken ct = default)
    {
        _ctx.Inventories.Update(inventory);
        await Task.CompletedTask;
    }

    public async Task<IEnumerable<Inventory>> GetLowStockAsync(CancellationToken ct = default)
        => await _ctx.Inventories.AsNoTracking()
            .Include(i => i.Product)
            .Where(i => i.AvailableStock <= i.LowStockThreshold)
            .ToListAsync(ct);
}

// ─────────────────────────────────────────────────────────────────────────────
// ORDER REPOSITORY
// ─────────────────────────────────────────────────────────────────────────────
public class OrderRepository : IOrderRepository
{
    private readonly AppDbContext _ctx;
    public OrderRepository(AppDbContext ctx) => _ctx = ctx;

    public async Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _ctx.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<Order?> GetByIdWithDetailsAsync(Guid id, CancellationToken ct = default)
        // Tracked — needed for state transitions (Complete, Cancel, Ship)
        => await _ctx.Orders.Include(o => o.Product)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<PagedResult<Order>> GetByUserIdAsync(Guid userId, int page,
        int pageSize, CancellationToken ct = default)
    {
        var query = _ctx.Orders.AsNoTracking()
            .Include(o => o.Product)
            .Where(o => o.UserId == userId);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<Order>(items, total, page, pageSize,
            (int)Math.Ceiling((double)total / pageSize));
    }

    public async Task<PagedResult<Order>> GetAllAsync(int page, int pageSize,
        string? status = null, CancellationToken ct = default)
    {
        var query = _ctx.Orders.AsNoTracking().Include(o => o.Product).AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<Domain.Enums.OrderStatus>(status, out var statusEnum))
            query = query.Where(o => o.Status == statusEnum);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<Order>(items, total, page, pageSize,
            (int)Math.Ceiling((double)total / pageSize));
    }

    public async Task<Order> AddAsync(Order order, CancellationToken ct = default)
    {
        await _ctx.Orders.AddAsync(order, ct);
        return order;
    }

    public async Task UpdateAsync(Order order, CancellationToken ct = default)
    {
        _ctx.Orders.Update(order);
        await Task.CompletedTask;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// INVOICE REPOSITORY
// ─────────────────────────────────────────────────────────────────────────────
public class InvoiceRepository : IInvoiceRepository
{
    private readonly AppDbContext _ctx;
    public InvoiceRepository(AppDbContext ctx) => _ctx = ctx;

    public async Task<Invoice?> GetByIdAsync(Guid id, CancellationToken ct = default)
        // Tracked — pay operation needs to update the entity
        => await _ctx.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<Invoice?> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default)
        => await _ctx.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.OrderId == orderId, ct);

    public async Task<PagedResult<Invoice>> GetByUserIdAsync(Guid userId, int page,
        int pageSize, CancellationToken ct = default)
    {
        var query = _ctx.Invoices.AsNoTracking().Where(i => i.UserId == userId);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(i => i.IssuedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<Invoice>(items, total, page, pageSize,
            (int)Math.Ceiling((double)total / pageSize));
    }

    public async Task<Invoice> AddAsync(Invoice invoice, CancellationToken ct = default)
    {
        await _ctx.Invoices.AddAsync(invoice, ct);
        return invoice;
    }

    public async Task UpdateAsync(Invoice invoice, CancellationToken ct = default)
    {
        _ctx.Invoices.Update(invoice);
        await Task.CompletedTask;
    }

    public async Task<IEnumerable<Invoice>> GetOverdueAsync(CancellationToken ct = default)
        => await _ctx.Invoices.AsNoTracking()
            .Where(i => i.Status == Domain.Enums.InvoiceStatus.Pending
                        && i.DueDate < DateTime.UtcNow)
            .ToListAsync(ct);
}

// ─────────────────────────────────────────────────────────────────────────────
// NOTIFICATION REPOSITORY
// ─────────────────────────────────────────────────────────────────────────────
public class NotificationRepository : INotificationRepository
{
    private readonly AppDbContext _ctx;
    public NotificationRepository(AppDbContext ctx) => _ctx = ctx;

    // Used by the background service to dequeue pending notifications for processing
    public async Task<IEnumerable<Notification>> GetPendingAsync(int batchSize,
        CancellationToken ct = default)
        // Tracked — background service will update status to Sent/Failed
        => await _ctx.Notifications
            .Where(n => n.Status == "Pending" && n.RetryCount < 3)
            .OrderBy(n => n.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

    public async Task<PagedResult<Notification>> GetByUserIdAsync(Guid userId, int page,
        int pageSize, CancellationToken ct = default)
    {
        var query = _ctx.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<Notification>(items, total, page, pageSize,
            (int)Math.Ceiling((double)total / pageSize));
    }

    public async Task<Notification> AddAsync(Notification notification, CancellationToken ct = default)
    {
        await _ctx.Notifications.AddAsync(notification, ct);
        return notification;
    }

    public async Task UpdateAsync(Notification notification, CancellationToken ct = default)
    {
        _ctx.Notifications.Update(notification);
        await Task.CompletedTask;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AUDIT LOG REPOSITORY
// ─────────────────────────────────────────────────────────────────────────────
public class AuditLogRepository : IAuditLogRepository
{
    private readonly AppDbContext _ctx;
    public AuditLogRepository(AppDbContext ctx) => _ctx = ctx;

    public async Task<PagedResult<AuditLog>> GetByEntityAsync(string entityName,
        string entityId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _ctx.AuditLogs.AsNoTracking()
            .Where(a => a.EntityName == entityName && a.EntityId == entityId);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<AuditLog>(items, total, page, pageSize,
            (int)Math.Ceiling((double)total / pageSize));
    }

    public async Task<AuditLog> AddAsync(AuditLog log, CancellationToken ct = default)
    {
        await _ctx.AuditLogs.AddAsync(log, ct);
        return log;
    }

    public async Task AddRangeAsync(IEnumerable<AuditLog> logs, CancellationToken ct = default)
        => await _ctx.AuditLogs.AddRangeAsync(logs, ct);
}
