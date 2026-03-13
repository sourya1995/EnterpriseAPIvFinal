using EnterpriseApi.Application.Interfaces;
using EnterpriseApi.Infrastructure.Data;
using EnterpriseApi.Infrastructure.Repositories;

namespace EnterpriseApi.Infrastructure;

/// <summary>
/// Coordinates all repository operations under a single DbContext transaction.
/// 
/// PATTERN: All operations within a single HTTP request share the same DbContext instance
/// (because both UnitOfWork and AppDbContext are Scoped). This means:
///   1. All EF change tracking is consistent across repositories
///   2. SaveChangesAsync() commits ALL pending changes atomically
///   3. No partial saves — either everything succeeds or nothing does
///
/// LAZY INITIALIZATION: Repositories are only created if used. A request that only
/// touches Users never allocates an OrderRepository or InventoryRepository.
/// This reduces heap allocation under load.
/// </summary>
public class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _ctx;

    private IUserRepository? _users;
    private IProductRepository? _products;
    private IInventoryRepository? _inventories;
    private IOrderRepository? _orders;
    private IInvoiceRepository? _invoices;
    private INotificationRepository? _notifications;
    private IAuditLogRepository? _auditLogs;

    public UnitOfWork(AppDbContext ctx) => _ctx = ctx;

    public IUserRepository Users => _users ??= new UserRepository(_ctx);
    public IProductRepository Products => _products ??= new ProductRepository(_ctx);
    public IInventoryRepository Inventories => _inventories ??= new InventoryRepository(_ctx);
    public IOrderRepository Orders => _orders ??= new OrderRepository(_ctx);
    public IInvoiceRepository Invoices => _invoices ??= new InvoiceRepository(_ctx);
    public INotificationRepository Notifications => _notifications ??= new NotificationRepository(_ctx);
    public IAuditLogRepository AuditLogs => _auditLogs ??= new AuditLogRepository(_ctx);

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _ctx.SaveChangesAsync(ct);

    public void Dispose() => _ctx.Dispose();
}
