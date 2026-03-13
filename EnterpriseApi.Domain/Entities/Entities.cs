using EnterpriseApi.Domain.Exceptions;

namespace EnterpriseApi.Domain.Entities;

// ─────────────────────────────────────────────────────────────────────────────
// USER
// ─────────────────────────────────────────────────────────────────────────────
public class User
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public string FirstName { get; private set; } = null!;
    public string LastName { get; private set; } = null!;
    public string Role { get; private set; } = null!;
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    // Concurrency token — EF Core uses this for optimistic concurrency.
    // Every UPDATE includes WHERE RowVersion = @original.
    // If another transaction modified the row, 0 rows affected → DbUpdateConcurrencyException.
    public uint RowVersion { get; private set; }

    private readonly List<Order> _orders = new();
    private readonly List<Notification> _notifications = new();
    private readonly List<Invoice> _invoices = new();

    public IReadOnlyCollection<Order> Orders => _orders.AsReadOnly();
    public IReadOnlyCollection<Notification> Notifications => _notifications.AsReadOnly();
    public IReadOnlyCollection<Invoice> Invoices => _invoices.AsReadOnly();

    private User() { }

    public static User Create(string email, string passwordHash,
        string firstName, string lastName, string role)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new InvariantViolationException("Email cannot be empty.");
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new InvariantViolationException("Password hash cannot be empty.");

        return new User
        {
            Id = Guid.NewGuid(),
            Email = email.ToLowerInvariant().Trim(),
            PasswordHash = passwordHash,
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            Role = role,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void UpdateProfile(string firstName, string lastName)
    {
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
            throw new InvariantViolationException("Name fields cannot be empty.");

        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    public void ChangeRole(string newRole)
    {
        if (newRole is not ("Admin" or "User" or "Manager"))
            throw new BusinessRuleException("INVALID_ROLE", $"Role '{newRole}' is not valid.");
        Role = newRole;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        if (!IsActive)
            throw new BusinessRuleException("ALREADY_DEACTIVATED", "User is already deactivated.");
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PRODUCT
// ─────────────────────────────────────────────────────────────────────────────
public class Product
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string Description { get; private set; } = null!;
    public string SKU { get; private set; } = null!;       // Stock Keeping Unit — unique identifier
    public string Category { get; private set; } = null!;
    public decimal BasePrice { get; private set; }
    public decimal? DiscountedPrice { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public uint RowVersion { get; private set; }

    public Inventory? Inventory { get; private set; }

    private readonly List<Order> _orders = new();
    public IReadOnlyCollection<Order> Orders => _orders.AsReadOnly();

    private Product() { }

    public static Product Create(string name, string description, string sku,
        string category, decimal basePrice)
    {
        if (basePrice <= 0)
            throw new InvariantViolationException("Base price must be positive.");
        if (string.IsNullOrWhiteSpace(sku))
            throw new InvariantViolationException("SKU cannot be empty.");

        return new Product
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Description = description,
            SKU = sku.ToUpperInvariant().Trim(),
            Category = category,
            BasePrice = basePrice,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
    }

    public decimal EffectivePrice => DiscountedPrice ?? BasePrice;

    public void ApplyDiscount(decimal discountedPrice)
    {
        if (discountedPrice <= 0 || discountedPrice >= BasePrice)
            throw new BusinessRuleException("INVALID_DISCOUNT",
                "Discounted price must be positive and less than base price.");
        DiscountedPrice = discountedPrice;
        UpdatedAt = DateTime.UtcNow;
    }

    public void RemoveDiscount()
    {
        DiscountedPrice = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateDetails(string name, string description, string category)
    {
        Name = name;
        Description = description;
        Category = category;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate() { IsActive = false; UpdatedAt = DateTime.UtcNow; }
    public void Activate() { IsActive = true; UpdatedAt = DateTime.UtcNow; }
}

// ─────────────────────────────────────────────────────────────────────────────
// INVENTORY
// ─────────────────────────────────────────────────────────────────────────────
public class Inventory
{
    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public int TotalStock { get; private set; }
    public int ReservedStock { get; private set; }    // Locked by pending orders
    public int LowStockThreshold { get; private set; }
    public DateTime LastUpdated { get; private set; }
    public uint RowVersion { get; private set; }      // Critical for concurrency

    public Product Product { get; private set; } = null!;

    // Computed property — AvailableStock is what customers can actually order
    public int AvailableStock => TotalStock - ReservedStock;
    public bool IsLowStock => AvailableStock <= LowStockThreshold;

    private Inventory() { }

    public static Inventory Create(Guid productId, int initialStock, int lowStockThreshold = 10)
    {
        if (initialStock < 0)
            throw new InvariantViolationException("Initial stock cannot be negative.");

        return new Inventory
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            TotalStock = initialStock,
            ReservedStock = 0,
            LowStockThreshold = lowStockThreshold,
            LastUpdated = DateTime.UtcNow
        };
    }

    // Reserve stock for a pending order. Throws if unavailable.
    // This is the hot-path method — called under concurrency pressure.
    public void Reserve(int quantity)
    {
        if (quantity <= 0)
            throw new InvariantViolationException("Reservation quantity must be positive.");
        if (quantity > AvailableStock)
            throw new InsufficientInventoryException(ProductId, quantity, AvailableStock);

        ReservedStock += quantity;
        LastUpdated = DateTime.UtcNow;
    }

    // Release reservation when order is cancelled or completed
    public void Release(int quantity)
    {
        if (quantity > ReservedStock)
            throw new InvariantViolationException(
                $"Cannot release {quantity} units; only {ReservedStock} are reserved.");

        ReservedStock -= quantity;
        LastUpdated = DateTime.UtcNow;
    }

    // Commit: when order completes, reserved → shipped (remove from total)
    public void Commit(int quantity)
    {
        if (quantity > ReservedStock)
            throw new InvariantViolationException("Cannot commit more than reserved.");

        ReservedStock -= quantity;
        TotalStock -= quantity;
        LastUpdated = DateTime.UtcNow;
    }

    public void Restock(int quantity)
    {
        if (quantity <= 0)
            throw new InvariantViolationException("Restock quantity must be positive.");

        TotalStock += quantity;
        LastUpdated = DateTime.UtcNow;
    }

    public void UpdateThreshold(int newThreshold)
    {
        if (newThreshold < 0)
            throw new InvariantViolationException("Low stock threshold cannot be negative.");
        LowStockThreshold = newThreshold;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ORDER
// ─────────────────────────────────────────────────────────────────────────────
public class Order
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }    // Snapshot of price at order time
    public decimal TotalAmount { get; private set; }
    public OrderStatus Status { get; private set; }
    public string? CancellationReason { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public uint RowVersion { get; private set; }

    public User User { get; private set; } = null!;
    public Product Product { get; private set; } = null!;
    public Invoice? Invoice { get; private set; }

    private Order() { }

    public static Order Create(Guid userId, Guid productId, int quantity, decimal unitPrice)
    {
        if (quantity <= 0)
            throw new InvariantViolationException("Order quantity must be positive.");
        if (unitPrice <= 0)
            throw new InvariantViolationException("Unit price must be positive.");

        return new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProductId = productId,
            Quantity = quantity,
            UnitPrice = unitPrice,
            TotalAmount = quantity * unitPrice,
            Status = OrderStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void ConfirmPayment()
    {
        if (Status != OrderStatus.Pending)
            throw new BusinessRuleException("INVALID_ORDER_TRANSITION",
                $"Cannot confirm payment for order in '{Status}' status.");
        Status = OrderStatus.Confirmed;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkShipped()
    {
        if (Status != OrderStatus.Confirmed)
            throw new BusinessRuleException("INVALID_ORDER_TRANSITION",
                $"Cannot ship order in '{Status}' status. Must be Confirmed first.");
        Status = OrderStatus.Shipped;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Complete()
    {
        if (Status != OrderStatus.Shipped)
            throw new BusinessRuleException("INVALID_ORDER_TRANSITION",
                $"Cannot complete order in '{Status}' status. Must be Shipped first.");
        Status = OrderStatus.Completed;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Cancel(string reason)
    {
        if (Status is OrderStatus.Completed or OrderStatus.Cancelled)
            throw new BusinessRuleException("INVALID_ORDER_TRANSITION",
                $"Cannot cancel an order in '{Status}' status.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvariantViolationException("Cancellation reason is required.");

        Status = OrderStatus.Cancelled;
        CancellationReason = reason;
        UpdatedAt = DateTime.UtcNow;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// INVOICE
// ─────────────────────────────────────────────────────────────────────────────
public class Invoice
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid UserId { get; private set; }
    public string InvoiceNumber { get; private set; } = null!;
    public decimal Subtotal { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal TotalAmount { get; private set; }
    public decimal TaxRate { get; private set; }
    public InvoiceStatus Status { get; private set; }
    public DateTime IssuedAt { get; private set; }
    public DateTime DueDate { get; private set; }
    public DateTime? PaidAt { get; private set; }
    public string? PaymentReference { get; private set; }
    public uint RowVersion { get; private set; }

    public Order Order { get; private set; } = null!;
    public User User { get; private set; } = null!;

    private Invoice() { }

    public static Invoice Create(Guid orderId, Guid userId, decimal subtotal,
        decimal taxRate = 0.20m)
    {
        if (subtotal <= 0) throw new InvariantViolationException("Subtotal must be positive.");
        if (taxRate < 0 || taxRate > 1)
            throw new InvariantViolationException("Tax rate must be between 0 and 1.");

        var taxAmount = Math.Round(subtotal * taxRate, 2);

        return new Invoice
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            UserId = userId,
            InvoiceNumber = GenerateInvoiceNumber(),
            Subtotal = subtotal,
            TaxAmount = taxAmount,
            TotalAmount = subtotal + taxAmount,
            TaxRate = taxRate,
            Status = InvoiceStatus.Pending,
            IssuedAt = DateTime.UtcNow,
            DueDate = DateTime.UtcNow.AddDays(30)
        };
    }

    public void MarkAsPaid(string paymentReference)
    {
        if (Status != InvoiceStatus.Pending)
            throw new InvalidInvoiceStateException(Status.ToString(), "Paid");
        if (string.IsNullOrWhiteSpace(paymentReference))
            throw new InvariantViolationException("Payment reference is required.");

        Status = InvoiceStatus.Paid;
        PaidAt = DateTime.UtcNow;
        PaymentReference = paymentReference;
    }

    public void Void()
    {
        if (Status == InvoiceStatus.Paid)
            throw new InvalidInvoiceStateException(Status.ToString(), "Void");
        Status = InvoiceStatus.Void;
    }

    public bool IsOverdue => Status == InvoiceStatus.Pending && DateTime.UtcNow > DueDate;

    private static string GenerateInvoiceNumber()
        => $"INV-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..8].ToUpper()}";
}

// ─────────────────────────────────────────────────────────────────────────────
// NOTIFICATION
// ─────────────────────────────────────────────────────────────────────────────
public class Notification
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Channel { get; private set; } = null!;   // Email | SMS | Push
    public string Subject { get; private set; } = null!;
    public string Body { get; private set; } = null!;
    public string Status { get; private set; } = null!;    // Pending | Sent | Failed
    public int RetryCount { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? SentAt { get; private set; }

    public User User { get; private set; } = null!;

    private Notification() { }

    public static Notification Create(Guid userId, string channel, string subject, string body)
    {
        return new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Channel = channel,
            Subject = subject,
            Body = body,
            Status = "Pending",
            RetryCount = 0,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void MarkSent()
    {
        Status = "Sent";
        SentAt = DateTime.UtcNow;
    }

    public void MarkFailed(string reason)
    {
        Status = "Failed";
        FailureReason = reason;
        RetryCount++;
    }

    public void ResetForRetry()
    {
        if (RetryCount >= 3)
            throw new BusinessRuleException("MAX_RETRIES_EXCEEDED",
                "Notification has exceeded maximum retry attempts.");
        Status = "Pending";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AUDIT LOG
// ─────────────────────────────────────────────────────────────────────────────
public class AuditLog
{
    public Guid Id { get; private set; }
    public string EntityName { get; private set; } = null!;
    public string EntityId { get; private set; } = null!;
    public string Action { get; private set; } = null!;     // Created | Modified | Deleted
    public string? OldValues { get; private set; }
    public string? NewValues { get; private set; }
    public Guid? UserId { get; private set; }              // Who performed the action
    public string? IpAddress { get; private set; }
    public DateTime Timestamp { get; private set; }

    private AuditLog() { }

    public static AuditLog Create(string entityName, string entityId, string action,
        string? oldValues, string? newValues, Guid? userId, string? ipAddress = null)
    {
        return new AuditLog
        {
            Id = Guid.NewGuid(),
            EntityName = entityName,
            EntityId = entityId,
            Action = action,
            OldValues = oldValues,
            NewValues = newValues,
            UserId = userId,
            IpAddress = ipAddress,
            Timestamp = DateTime.UtcNow
        };
    }
}
