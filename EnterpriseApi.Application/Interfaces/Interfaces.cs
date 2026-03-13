using EnterpriseApi.Application.DTOs;
using EnterpriseApi.Domain.Entities;
using EnterpriseApi.Domain.Events;
using System.Security.Claims;

namespace EnterpriseApi.Application.Interfaces;

// ─── Repositories ─────────────────────────────────────────────────────────────

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<PagedResult<User>> GetAllAsync(int page, int pageSize, CancellationToken ct = default);
    Task<User> AddAsync(User user, CancellationToken ct = default);
    Task UpdateAsync(User user, CancellationToken ct = default);
    Task<bool> ExistsAsync(string email, CancellationToken ct = default);
}

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Product?> GetByIdWithInventoryAsync(Guid id, CancellationToken ct = default);
    Task<Product?> GetBySkuAsync(string sku, CancellationToken ct = default);
    Task<PagedResult<Product>> GetAllAsync(int page, int pageSize, string? category = null,
        CancellationToken ct = default);
    Task<Product> AddAsync(Product product, CancellationToken ct = default);
    Task UpdateAsync(Product product, CancellationToken ct = default);
    Task<IEnumerable<Product>> GetLowStockProductsAsync(CancellationToken ct = default);
}

public interface IInventoryRepository
{
    Task<Inventory?> GetByProductIdAsync(Guid productId, CancellationToken ct = default);
    Task<Inventory?> GetByProductIdForUpdateAsync(Guid productId, CancellationToken ct = default); // Tracked
    Task<Inventory> AddAsync(Inventory inventory, CancellationToken ct = default);
    Task UpdateAsync(Inventory inventory, CancellationToken ct = default);
    Task<IEnumerable<Inventory>> GetLowStockAsync(CancellationToken ct = default);
}

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Order?> GetByIdWithDetailsAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<Order>> GetByUserIdAsync(Guid userId, int page, int pageSize,
        CancellationToken ct = default);
    Task<PagedResult<Order>> GetAllAsync(int page, int pageSize, string? status = null,
        CancellationToken ct = default);
    Task<Order> AddAsync(Order order, CancellationToken ct = default);
    Task UpdateAsync(Order order, CancellationToken ct = default);
}

public interface IInvoiceRepository
{
    Task<Invoice?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Invoice?> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default);
    Task<PagedResult<Invoice>> GetByUserIdAsync(Guid userId, int page, int pageSize,
        CancellationToken ct = default);
    Task<Invoice> AddAsync(Invoice invoice, CancellationToken ct = default);
    Task UpdateAsync(Invoice invoice, CancellationToken ct = default);
    Task<IEnumerable<Invoice>> GetOverdueAsync(CancellationToken ct = default);
}

public interface INotificationRepository
{
    Task<IEnumerable<Notification>> GetPendingAsync(int batchSize, CancellationToken ct = default);
    Task<PagedResult<Notification>> GetByUserIdAsync(Guid userId, int page, int pageSize,
        CancellationToken ct = default);
    Task<Notification> AddAsync(Notification notification, CancellationToken ct = default);
    Task UpdateAsync(Notification notification, CancellationToken ct = default);
}

public interface IAuditLogRepository
{
    Task<PagedResult<AuditLog>> GetByEntityAsync(string entityName, string entityId,
        int page, int pageSize, CancellationToken ct = default);
    Task<AuditLog> AddAsync(AuditLog log, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<AuditLog> logs, CancellationToken ct = default);
}

// ─── Unit of Work ─────────────────────────────────────────────────────────────
// Coordinates multiple repository operations in a single database transaction.
// CRITICAL: Call SaveChangesAsync() ONCE per business operation, not per repository call.

public interface IUnitOfWork : IDisposable
{
    IUserRepository Users { get; }
    IProductRepository Products { get; }
    IInventoryRepository Inventories { get; }
    IOrderRepository Orders { get; }
    IInvoiceRepository Invoices { get; }
    INotificationRepository Notifications { get; }
    IAuditLogRepository AuditLogs { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

// ─── Application Services ─────────────────────────────────────────────────────

public interface IUserService
{
    Task<PagedResult<UserDto>> GetAllAsync(int page, int pageSize, CancellationToken ct = default);
    Task<UserDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default);
    Task<UserDto> ChangeRoleAsync(Guid id, ChangeRoleRequest request, CancellationToken ct = default);
    Task DeactivateAsync(Guid id, CancellationToken ct = default);
    Task ActivateAsync(Guid id, CancellationToken ct = default);
}

public interface IProductService
{
    Task<PagedResult<ProductDto>> GetAllAsync(int page, int pageSize, string? category = null,
        CancellationToken ct = default);
    Task<ProductDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ProductDto> CreateAsync(CreateProductRequest request, CancellationToken ct = default);
    Task<ProductDto> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken ct = default);
    Task<ProductDto> ApplyDiscountAsync(Guid id, ApplyDiscountRequest request,
        CancellationToken ct = default);
    Task<ProductDto> RestockAsync(Guid id, RestockRequest request, CancellationToken ct = default);
    Task DeactivateAsync(Guid id, CancellationToken ct = default);
}

public interface IOrderService
{
    Task<OrderDto> CreateOrderAsync(Guid userId, CreateOrderRequest request,
        CancellationToken ct = default);
    Task<OrderDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<OrderDto>> GetByUserIdAsync(Guid userId, int page, int pageSize,
        CancellationToken ct = default);
    Task<PagedResult<OrderDto>> GetAllAsync(int page, int pageSize, string? status = null,
        CancellationToken ct = default);
    Task<OrderDto> CancelOrderAsync(Guid id, CancelOrderRequest request,
        CancellationToken ct = default);
    Task<OrderDto> CompleteOrderAsync(Guid id, CancellationToken ct = default);
    Task<OrderDto> ShipOrderAsync(Guid id, CancellationToken ct = default);
}

public interface IInvoiceService
{
    Task<InvoiceDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<InvoiceDto> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default);
    Task<PagedResult<InvoiceDto>> GetByUserIdAsync(Guid userId, int page, int pageSize,
        CancellationToken ct = default);
    Task<InvoiceDto> PayInvoiceAsync(Guid id, PayInvoiceRequest request,
        CancellationToken ct = default);
    Task<IEnumerable<InvoiceDto>> GetOverdueAsync(CancellationToken ct = default);
}

public interface INotificationService
{
    Task<PagedResult<NotificationDto>> GetByUserIdAsync(Guid userId, int page, int pageSize,
        CancellationToken ct = default);
    Task SendAsync(Guid userId, string channel, string subject, string body,
        CancellationToken ct = default);
}

public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
}

// ─── Token Service ────────────────────────────────────────────────────────────
public interface ITokenService
{
    string GenerateToken(User user);
    ClaimsPrincipal? ValidateToken(string token);
}

// ─── Event Publisher (Kafka abstraction) ──────────────────────────────────────
// The Application layer depends on this interface, not Kafka directly.
// This preserves the Dependency Rule: Application knows nothing about Confluent.Kafka.

public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : DomainEvent;

    Task PublishManyAsync<TEvent>(IEnumerable<TEvent> events, CancellationToken ct = default)
        where TEvent : DomainEvent;
}

// ─── Current User Context ─────────────────────────────────────────────────────
// Wraps IHttpContextAccessor for use in services without HTTP coupling.
public interface ICurrentUserService
{
    Guid? UserId { get; }
    string? UserEmail { get; }
    string? UserRole { get; }
    string? IpAddress { get; }
    bool IsAuthenticated { get; }
}

// ─── Audit Service ────────────────────────────────────────────────────────────
public interface IAuditService
{
    Task LogAsync(string entityName, string entityId, string action,
        string? oldValues, string? newValues, CancellationToken ct = default);
    Task<PagedResult<AuditLogDto>> GetEntityHistoryAsync(string entityName, string entityId,
        int page, int pageSize, CancellationToken ct = default);
}
