namespace EnterpriseApi.Application.DTOs;

// ─── Pagination ───────────────────────────────────────────────────────────────
public record PagedResult<T>(
    IEnumerable<T> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages
);

// ─── User DTOs ────────────────────────────────────────────────────────────────
public record UserDto(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    string Role,
    bool IsActive,
    DateTime CreatedAt
);

public record CreateUserRequest(
    string Email,
    string Password,
    string FirstName,
    string LastName,
    string Role
);

public record UpdateUserRequest(string FirstName, string LastName);
public record ChangeRoleRequest(string Role);

// ─── Auth DTOs ────────────────────────────────────────────────────────────────
public record LoginRequest(string Email, string Password);
public record RegisterRequest(string Email, string Password, string FirstName, string LastName);
public record AuthResponse(string Token, string Email, string Role, DateTime ExpiresAt);

// ─── Product DTOs ─────────────────────────────────────────────────────────────
public record ProductDto(
    Guid Id,
    string Name,
    string Description,
    string SKU,
    string Category,
    decimal BasePrice,
    decimal? DiscountedPrice,
    decimal EffectivePrice,
    bool IsActive,
    InventoryDto? Inventory
);

public record CreateProductRequest(
    string Name,
    string Description,
    string SKU,
    string Category,
    decimal BasePrice,
    int InitialStock,
    int LowStockThreshold = 10
);

public record UpdateProductRequest(string Name, string Description, string Category);
public record ApplyDiscountRequest(decimal DiscountedPrice);
public record RestockRequest(int Quantity);

// ─── Inventory DTOs ───────────────────────────────────────────────────────────
public record InventoryDto(
    Guid ProductId,
    int TotalStock,
    int ReservedStock,
    int AvailableStock,
    int LowStockThreshold,
    bool IsLowStock,
    DateTime LastUpdated
);

// ─── Order DTOs ───────────────────────────────────────────────────────────────
public record OrderDto(
    Guid Id,
    Guid UserId,
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal TotalAmount,
    string Status,
    string? CancellationReason,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record CreateOrderRequest(
    Guid ProductId,
    int Quantity
);

public record CancelOrderRequest(string Reason);

// ─── Invoice DTOs ─────────────────────────────────────────────────────────────
public record InvoiceDto(
    Guid Id,
    Guid OrderId,
    string InvoiceNumber,
    decimal Subtotal,
    decimal TaxAmount,
    decimal TotalAmount,
    decimal TaxRate,
    string Status,
    DateTime IssuedAt,
    DateTime DueDate,
    DateTime? PaidAt,
    string? PaymentReference,
    bool IsOverdue
);

public record PayInvoiceRequest(string PaymentReference);

// ─── Notification DTOs ────────────────────────────────────────────────────────
public record NotificationDto(
    Guid Id,
    Guid UserId,
    string Channel,
    string Subject,
    string Status,
    int RetryCount,
    DateTime CreatedAt,
    DateTime? SentAt
);

// ─── Audit DTOs ───────────────────────────────────────────────────────────────
public record AuditLogDto(
    Guid Id,
    string EntityName,
    string EntityId,
    string Action,
    string? OldValues,
    string? NewValues,
    Guid? UserId,
    DateTime Timestamp
);
