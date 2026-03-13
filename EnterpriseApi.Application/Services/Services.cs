using EnterpriseApi.Application.DTOs;
using EnterpriseApi.Application.Interfaces;
using EnterpriseApi.Domain.Entities;
using EnterpriseApi.Domain.Events;
using EnterpriseApi.Domain.Exceptions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace EnterpriseApi.Application.Services;

// ─────────────────────────────────────────────────────────────────────────────
// AUTH SERVICE
// ─────────────────────────────────────────────────────────────────────────────
public class AuthService : IAuthService
{
    private readonly IUnitOfWork _uow;
    private readonly ITokenService _tokenService;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<AuthService> _logger;

    public AuthService(IUnitOfWork uow, ITokenService tokenService,
        IPasswordHasher<User> passwordHasher, IEventPublisher eventPublisher,
        ILogger<AuthService> logger)
    {
        _uow = uow;
        _tokenService = tokenService;
        _passwordHasher = passwordHasher;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var user = await _uow.Users.GetByEmailAsync(request.Email.ToLowerInvariant(), ct);

        // SECURITY: Timing attack prevention — always hash even if user not found.
        // This ensures response time is equal for valid/invalid emails.
        if (user is null || !user.IsActive)
        {
            _passwordHasher.HashPassword(null!, request.Password);
            _logger.LogWarning("Failed login for email: {Email}", request.Email);
            throw new UnauthorizedException("Invalid credentials.");
        }

        var result = _passwordHasher.VerifyHashedPassword(null!, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning("Wrong password for user {UserId}", user.Id);
            throw new UnauthorizedException("Invalid credentials.");
        }

        var token = _tokenService.GenerateToken(user);
        _logger.LogInformation("User {UserId} authenticated", user.Id);
        return new AuthResponse(token, user.Email, user.Role, DateTime.UtcNow.AddHours(1));
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        if (await _uow.Users.ExistsAsync(request.Email, ct))
            throw new DuplicateException("User", "email", request.Email);

        var hash = _passwordHasher.HashPassword(null!, request.Password);
        var user = User.Create(request.Email, hash, request.FirstName, request.LastName, "User");

        await _uow.Users.AddAsync(user, ct);
        await _uow.SaveChangesAsync(ct);

        // Publish domain event — Kafka consumer will trigger welcome email
        await _eventPublisher.PublishAsync(new UserRegisteredEvent
        {
            UserId = user.Id,
            Email = user.Email,
            FirstName = user.FirstName
        }, ct);

        var token = _tokenService.GenerateToken(user);
        return new AuthResponse(token, user.Email, user.Role, DateTime.UtcNow.AddHours(1));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// USER SERVICE
// ─────────────────────────────────────────────────────────────────────────────
public class UserService : IUserService
{
    private readonly IUnitOfWork _uow;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly ILogger<UserService> _logger;

    public UserService(IUnitOfWork uow, IPasswordHasher<User> passwordHasher,
        ILogger<UserService> logger)
    {
        _uow = uow;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    public async Task<PagedResult<UserDto>> GetAllAsync(int page, int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await _uow.Users.GetAllAsync(page, pageSize, ct);
        return new PagedResult<UserDto>(result.Items.Select(MapToDto),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages);
    }

    public async Task<UserDto> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var user = await _uow.Users.GetByIdAsync(id, ct)
            ?? throw new NotFoundException(nameof(User), id);
        return MapToDto(user);
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        if (await _uow.Users.ExistsAsync(request.Email, ct))
            throw new DuplicateException("User", "email", request.Email);

        var hash = _passwordHasher.HashPassword(null!, request.Password);
        var user = User.Create(request.Email, hash, request.FirstName, request.LastName, request.Role);
        await _uow.Users.AddAsync(user, ct);
        await _uow.SaveChangesAsync(ct);
        _logger.LogInformation("User {UserId} created by admin", user.Id);
        return MapToDto(user);
    }

    public async Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest request,
        CancellationToken ct = default)
    {
        var user = await _uow.Users.GetByIdAsync(id, ct)
            ?? throw new NotFoundException(nameof(User), id);
        user.UpdateProfile(request.FirstName, request.LastName);
        await _uow.Users.UpdateAsync(user, ct);
        await _uow.SaveChangesAsync(ct);
        return MapToDto(user);
    }

    public async Task<UserDto> ChangeRoleAsync(Guid id, ChangeRoleRequest request,
        CancellationToken ct = default)
    {
        var user = await _uow.Users.GetByIdAsync(id, ct)
            ?? throw new NotFoundException(nameof(User), id);
        user.ChangeRole(request.Role);
        await _uow.Users.UpdateAsync(user, ct);
        await _uow.SaveChangesAsync(ct);
        _logger.LogWarning("Role changed for user {UserId} to {Role}", id, request.Role);
        return MapToDto(user);
    }

    public async Task DeactivateAsync(Guid id, CancellationToken ct = default)
    {
        var user = await _uow.Users.GetByIdAsync(id, ct)
            ?? throw new NotFoundException(nameof(User), id);
        user.Deactivate();
        await _uow.Users.UpdateAsync(user, ct);
        await _uow.SaveChangesAsync(ct);
    }

    public async Task ActivateAsync(Guid id, CancellationToken ct = default)
    {
        var user = await _uow.Users.GetByIdAsync(id, ct)
            ?? throw new NotFoundException(nameof(User), id);
        user.Activate();
        await _uow.Users.UpdateAsync(user, ct);
        await _uow.SaveChangesAsync(ct);
    }

    private static UserDto MapToDto(User u) =>
        new(u.Id, u.Email, u.FirstName, u.LastName, u.Role, u.IsActive, u.CreatedAt);
}

// ─────────────────────────────────────────────────────────────────────────────
// PRODUCT SERVICE
// ─────────────────────────────────────────────────────────────────────────────
public class ProductService : IProductService
{
    private readonly IUnitOfWork _uow;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<ProductService> _logger;

    public ProductService(IUnitOfWork uow, IEventPublisher eventPublisher,
        ILogger<ProductService> logger)
    {
        _uow = uow;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<PagedResult<ProductDto>> GetAllAsync(int page, int pageSize,
        string? category = null, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await _uow.Products.GetAllAsync(page, pageSize, category, ct);
        return new PagedResult<ProductDto>(result.Items.Select(MapToDto),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages);
    }

    public async Task<ProductDto> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var product = await _uow.Products.GetByIdWithInventoryAsync(id, ct)
            ?? throw new NotFoundException(nameof(Product), id);
        return MapToDto(product);
    }

    public async Task<ProductDto> CreateAsync(CreateProductRequest request,
        CancellationToken ct = default)
    {
        if (await _uow.Products.GetBySkuAsync(request.SKU, ct) is not null)
            throw new DuplicateException("Product", "SKU", request.SKU);

        var product = Product.Create(request.Name, request.Description, request.SKU,
            request.Category, request.BasePrice);

        var inventory = Inventory.Create(product.Id, request.InitialStock,
            request.LowStockThreshold);

        await _uow.Products.AddAsync(product, ct);
        await _uow.Inventories.AddAsync(inventory, ct);
        await _uow.SaveChangesAsync(ct);

        _logger.LogInformation("Product {ProductId} ({SKU}) created", product.Id, product.SKU);
        return MapToDto(product);
    }

    public async Task<ProductDto> UpdateAsync(Guid id, UpdateProductRequest request,
        CancellationToken ct = default)
    {
        var product = await _uow.Products.GetByIdWithInventoryAsync(id, ct)
            ?? throw new NotFoundException(nameof(Product), id);

        product.UpdateDetails(request.Name, request.Description, request.Category);
        await _uow.Products.UpdateAsync(product, ct);
        await _uow.SaveChangesAsync(ct);
        return MapToDto(product);
    }

    public async Task<ProductDto> ApplyDiscountAsync(Guid id, ApplyDiscountRequest request,
        CancellationToken ct = default)
    {
        var product = await _uow.Products.GetByIdWithInventoryAsync(id, ct)
            ?? throw new NotFoundException(nameof(Product), id);

        product.ApplyDiscount(request.DiscountedPrice);
        await _uow.Products.UpdateAsync(product, ct);
        await _uow.SaveChangesAsync(ct);
        return MapToDto(product);
    }

    public async Task<ProductDto> RestockAsync(Guid id, RestockRequest request,
        CancellationToken ct = default)
    {
        var product = await _uow.Products.GetByIdWithInventoryAsync(id, ct)
            ?? throw new NotFoundException(nameof(Product), id);

        var inventory = await _uow.Inventories.GetByProductIdForUpdateAsync(id, ct)
            ?? throw new NotFoundException(nameof(Inventory), id);

        inventory.Restock(request.Quantity);
        await _uow.Inventories.UpdateAsync(inventory, ct);
        await _uow.SaveChangesAsync(ct);

        await _eventPublisher.PublishAsync(new InventoryRestockedEvent
        {
            ProductId = product.Id,
            QuantityAdded = request.Quantity,
            NewTotalStock = inventory.TotalStock
        }, ct);

        return MapToDto(product);
    }

    public async Task DeactivateAsync(Guid id, CancellationToken ct = default)
    {
        var product = await _uow.Products.GetByIdAsync(id, ct)
            ?? throw new NotFoundException(nameof(Product), id);
        product.Deactivate();
        await _uow.Products.UpdateAsync(product, ct);
        await _uow.SaveChangesAsync(ct);
    }

    private static ProductDto MapToDto(Product p) => new(
        p.Id, p.Name, p.Description, p.SKU, p.Category,
        p.BasePrice, p.DiscountedPrice, p.EffectivePrice, p.IsActive,
        p.Inventory is null ? null : new InventoryDto(
            p.Inventory.ProductId, p.Inventory.TotalStock, p.Inventory.ReservedStock,
            p.Inventory.AvailableStock, p.Inventory.LowStockThreshold,
            p.Inventory.IsLowStock, p.Inventory.LastUpdated));
}

// ─────────────────────────────────────────────────────────────────────────────
// ORDER SERVICE — contains the most critical concurrency logic
// ─────────────────────────────────────────────────────────────────────────────
public class OrderService : IOrderService
{
    private readonly IUnitOfWork _uow;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<OrderService> _logger;

    // SemaphoreSlim for per-product concurrency control.
    // Without this, two simultaneous orders for the same product could both read
    // AvailableStock=1, both Reserve(1), and both succeed — overselling inventory.
    // This in-memory lock prevents that within a single process instance.
    // For multi-instance deployments, use a distributed lock (Redis Redlock).
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _productLocks = new();

    public OrderService(IUnitOfWork uow, IEventPublisher eventPublisher,
        ILogger<OrderService> logger)
    {
        _uow = uow;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<OrderDto> CreateOrderAsync(Guid userId, CreateOrderRequest request,
        CancellationToken ct = default)
    {
        // Get or create a per-product semaphore (max 1 concurrent reservation per product)
        var semaphore = _productLocks.GetOrAdd(request.ProductId, _ => new SemaphoreSlim(1, 1));

        // Acquire the lock — wait up to 10 seconds
        var acquired = await semaphore.WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (!acquired)
            throw new BusinessRuleException("LOCK_TIMEOUT",
                "Could not acquire inventory lock. Please retry.");

        try
        {
            // Re-read product inside the lock with tracked inventory for update
            var product = await _uow.Products.GetByIdWithInventoryAsync(request.ProductId, ct)
                ?? throw new NotFoundException(nameof(Product), request.ProductId);

            if (!product.IsActive)
                throw new BusinessRuleException("PRODUCT_INACTIVE",
                    $"Product '{product.Name}' is not available for purchase.");

            var inventory = await _uow.Inventories.GetByProductIdForUpdateAsync(
                request.ProductId, ct)
                ?? throw new NotFoundException(nameof(Inventory), request.ProductId);

            // Domain logic throws InsufficientInventoryException if not enough stock
            inventory.Reserve(request.Quantity);

            var order = Order.Create(userId, product.Id, request.Quantity, product.EffectivePrice);

            await _uow.Orders.AddAsync(order, ct);
            await _uow.Inventories.UpdateAsync(inventory, ct);

            // SaveChanges wraps both operations in one DB transaction
            await _uow.SaveChangesAsync(ct);

            _logger.LogInformation("Order {OrderId} created, inventory reserved for product {ProductId}",
                order.Id, product.Id);

            // Publish event AFTER successful save — Kafka producer is fire-and-forget here
            await _eventPublisher.PublishAsync(new OrderCreatedEvent
            {
                OrderId = order.Id,
                UserId = userId,
                ProductId = product.Id,
                Quantity = request.Quantity,
                TotalAmount = order.TotalAmount,
                ProductName = product.Name
            }, ct);

            // Check if low stock after reservation
            if (inventory.IsLowStock)
            {
                await _eventPublisher.PublishAsync(new InventoryLowStockEvent
                {
                    ProductId = product.Id,
                    ProductName = product.Name,
                    CurrentStock = inventory.AvailableStock,
                    ThresholdLevel = inventory.LowStockThreshold
                }, ct);
            }

            return MapToDto(order, product.Name);
        }
        finally
        {
            semaphore.Release(); // Always release — even on exception
        }
    }

    public async Task<OrderDto> CancelOrderAsync(Guid id, CancelOrderRequest request,
        CancellationToken ct = default)
    {
        var order = await _uow.Orders.GetByIdWithDetailsAsync(id, ct)
            ?? throw new NotFoundException(nameof(Order), id);

        var semaphore = _productLocks.GetOrAdd(order.ProductId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);

        try
        {
            var inventory = await _uow.Inventories.GetByProductIdForUpdateAsync(order.ProductId, ct)
                ?? throw new NotFoundException(nameof(Inventory), order.ProductId);

            order.Cancel(request.Reason);
            inventory.Release(order.Quantity);

            await _uow.Orders.UpdateAsync(order, ct);
            await _uow.Inventories.UpdateAsync(inventory, ct);
            await _uow.SaveChangesAsync(ct);

            await _eventPublisher.PublishAsync(new OrderCancelledEvent
            {
                OrderId = order.Id,
                UserId = order.UserId,
                ProductId = order.ProductId,
                Quantity = order.Quantity,
                CancellationReason = request.Reason
            }, ct);

            return MapToDto(order, order.Product?.Name ?? "Unknown");
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<OrderDto> CompleteOrderAsync(Guid id, CancellationToken ct = default)
    {
        var order = await _uow.Orders.GetByIdWithDetailsAsync(id, ct)
            ?? throw new NotFoundException(nameof(Order), id);

        var semaphore = _productLocks.GetOrAdd(order.ProductId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);

        try
        {
            var inventory = await _uow.Inventories.GetByProductIdForUpdateAsync(order.ProductId, ct)
                ?? throw new NotFoundException(nameof(Inventory), order.ProductId);

            order.Complete();
            inventory.Commit(order.Quantity); // Remove from both reserved AND total

            // Generate invoice automatically on completion
            var invoice = Invoice.Create(order.Id, order.UserId, order.TotalAmount);

            await _uow.Orders.UpdateAsync(order, ct);
            await _uow.Inventories.UpdateAsync(inventory, ct);
            await _uow.Invoices.AddAsync(invoice, ct);
            await _uow.SaveChangesAsync(ct);

            await _eventPublisher.PublishAsync(new OrderCompletedEvent
            {
                OrderId = order.Id,
                UserId = order.UserId,
                InvoiceId = invoice.Id,
                Amount = order.TotalAmount
            }, ct);

            await _eventPublisher.PublishAsync(new InvoiceGeneratedEvent
            {
                InvoiceId = invoice.Id,
                OrderId = order.Id,
                UserId = order.UserId,
                Amount = invoice.TotalAmount
            }, ct);

            return MapToDto(order, order.Product?.Name ?? "Unknown");
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<OrderDto> ShipOrderAsync(Guid id, CancellationToken ct = default)
    {
        var order = await _uow.Orders.GetByIdWithDetailsAsync(id, ct)
            ?? throw new NotFoundException(nameof(Order), id);

        order.MarkShipped();
        await _uow.Orders.UpdateAsync(order, ct);
        await _uow.SaveChangesAsync(ct);
        return MapToDto(order, order.Product?.Name ?? "Unknown");
    }

    public async Task<OrderDto> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var order = await _uow.Orders.GetByIdWithDetailsAsync(id, ct)
            ?? throw new NotFoundException(nameof(Order), id);
        return MapToDto(order, order.Product?.Name ?? "Unknown");
    }

    public async Task<PagedResult<OrderDto>> GetByUserIdAsync(Guid userId, int page,
        int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await _uow.Orders.GetByUserIdAsync(userId, page, pageSize, ct);
        return new PagedResult<OrderDto>(result.Items.Select(o => MapToDto(o, o.Product?.Name ?? "")),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages);
    }

    public async Task<PagedResult<OrderDto>> GetAllAsync(int page, int pageSize,
        string? status = null, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await _uow.Orders.GetAllAsync(page, pageSize, status, ct);
        return new PagedResult<OrderDto>(result.Items.Select(o => MapToDto(o, o.Product?.Name ?? "")),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages);
    }

    private static OrderDto MapToDto(Order o, string productName) =>
        new(o.Id, o.UserId, o.ProductId, productName, o.Quantity, o.UnitPrice,
            o.TotalAmount, o.Status.ToString(), o.CancellationReason, o.CreatedAt, o.UpdatedAt);
}

// ─────────────────────────────────────────────────────────────────────────────
// INVOICE SERVICE
// ─────────────────────────────────────────────────────────────────────────────
public class InvoiceService : IInvoiceService
{
    private readonly IUnitOfWork _uow;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<InvoiceService> _logger;

    public InvoiceService(IUnitOfWork uow, IEventPublisher eventPublisher,
        ILogger<InvoiceService> logger)
    {
        _uow = uow;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<InvoiceDto> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var invoice = await _uow.Invoices.GetByIdAsync(id, ct)
            ?? throw new NotFoundException(nameof(Invoice), id);
        return MapToDto(invoice);
    }

    public async Task<InvoiceDto> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default)
    {
        var invoice = await _uow.Invoices.GetByOrderIdAsync(orderId, ct)
            ?? throw new NotFoundException(nameof(Invoice), $"OrderId={orderId}");
        return MapToDto(invoice);
    }

    public async Task<PagedResult<InvoiceDto>> GetByUserIdAsync(Guid userId, int page,
        int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await _uow.Invoices.GetByUserIdAsync(userId, page, pageSize, ct);
        return new PagedResult<InvoiceDto>(result.Items.Select(MapToDto),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages);
    }

    public async Task<InvoiceDto> PayInvoiceAsync(Guid id, PayInvoiceRequest request,
        CancellationToken ct = default)
    {
        var invoice = await _uow.Invoices.GetByIdAsync(id, ct)
            ?? throw new NotFoundException(nameof(Invoice), id);

        invoice.MarkAsPaid(request.PaymentReference);
        await _uow.Invoices.UpdateAsync(invoice, ct);
        await _uow.SaveChangesAsync(ct);

        await _eventPublisher.PublishAsync(new InvoicePaidEvent
        {
            InvoiceId = invoice.Id,
            UserId = invoice.UserId,
            AmountPaid = invoice.TotalAmount
        }, ct);

        _logger.LogInformation("Invoice {InvoiceId} paid with reference {Ref}",
            id, request.PaymentReference);

        return MapToDto(invoice);
    }

    public async Task<IEnumerable<InvoiceDto>> GetOverdueAsync(CancellationToken ct = default)
    {
        var invoices = await _uow.Invoices.GetOverdueAsync(ct);
        return invoices.Select(MapToDto);
    }

    private static InvoiceDto MapToDto(Invoice i) =>
        new(i.Id, i.OrderId, i.InvoiceNumber, i.Subtotal, i.TaxAmount,
            i.TotalAmount, i.TaxRate, i.Status.ToString(), i.IssuedAt,
            i.DueDate, i.PaidAt, i.PaymentReference, i.IsOverdue);
}

// ─────────────────────────────────────────────────────────────────────────────
// NOTIFICATION SERVICE
// ─────────────────────────────────────────────────────────────────────────────
public class NotificationService : INotificationService
{
    private readonly IUnitOfWork _uow;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(IUnitOfWork uow, ILogger<NotificationService> logger)
    {
        _uow = uow;
        _logger = logger;
    }

    public async Task<PagedResult<NotificationDto>> GetByUserIdAsync(Guid userId, int page,
        int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await _uow.Notifications.GetByUserIdAsync(userId, page, pageSize, ct);
        return new PagedResult<NotificationDto>(result.Items.Select(MapToDto),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages);
    }

    public async Task SendAsync(Guid userId, string channel, string subject,
        string body, CancellationToken ct = default)
    {
        var notification = Notification.Create(userId, channel, subject, body);
        await _uow.Notifications.AddAsync(notification, ct);
        await _uow.SaveChangesAsync(ct);
        _logger.LogInformation("Notification queued for user {UserId} via {Channel}", userId, channel);
    }

    private static NotificationDto MapToDto(Notification n) =>
        new(n.Id, n.UserId, n.Channel, n.Subject, n.Status, n.RetryCount, n.CreatedAt, n.SentAt);
}

// ─────────────────────────────────────────────────────────────────────────────
// AUDIT SERVICE
// ─────────────────────────────────────────────────────────────────────────────
public class AuditService : IAuditService
{
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUserService _currentUser;

    public AuditService(IUnitOfWork uow, ICurrentUserService currentUser)
    {
        _uow = uow;
        _currentUser = currentUser;
    }

    public async Task LogAsync(string entityName, string entityId, string action,
        string? oldValues, string? newValues, CancellationToken ct = default)
    {
        var log = AuditLog.Create(entityName, entityId, action, oldValues, newValues,
            _currentUser.UserId, _currentUser.IpAddress);
        await _uow.AuditLogs.AddAsync(log, ct);
        await _uow.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<AuditLogDto>> GetEntityHistoryAsync(string entityName,
        string entityId, int page, int pageSize, CancellationToken ct = default)
    {
        var result = await _uow.AuditLogs.GetByEntityAsync(entityName, entityId, page, pageSize, ct);
        return new PagedResult<AuditLogDto>(result.Items.Select(MapToDto),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages);
    }

    private static AuditLogDto MapToDto(AuditLog l) =>
        new(l.Id, l.EntityName, l.EntityId, l.Action, l.OldValues, l.NewValues,
            l.UserId, l.Timestamp);
}
