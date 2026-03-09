using EnterpriseApi.Domain.Enums;
using EnterpriseApi.Domain.Events;
using EnterpriseApi.Domain.Exceptions;
namespace EnterpriseApi.Domain.Entities;

//USER
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
    private User() { } // EF Core requires a parameterless constructor
    public static User Create(string email, string passwordHash, string firstName, string lastName, string role)
    {
        if (string.IsNullOrWhiteSpace(email)) throw new InvariantViolationException("Email cannot be empty.");
        if (string.IsNullOrWhiteSpace(passwordHash)) throw new InvariantViolationException("Password cannot be empty.");

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

    public void UpdateProfile(string? firstName, string? lastName)
    {
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
            throw new InvariantViolationException("At least one of first name or last name must be provided.");

        FirstName = firstName?.Trim();
        LastName = lastName?.Trim();
        UpdatedAt = DateTime.UtcNow;

    }

    public void ChangeRole(string newRole)
    {
        if (newRole is not ("Admin" or "User" or "Manager")) throw new BusinessRuleException("INVALID_ROLE", $"Role {newRole} is not valid.");
        Role = newRole;
        UpdatedAt = DateTime.UtcNow;

    }

    public void Deactivate()
    {
        if (!IsActive) throw new BusinessRuleException("ALREADY_INACTIVE", "User is already inactive.");
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        if (IsActive) throw new BusinessRuleException("ALREADY_ACTIVE", "User is already active.");
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }


}

// PRODUCT
public class Product
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string Description { get; private set; } = null!;
    public string SKU { get; private set; } = null!; // Stock Keeping Unit — unique identifier
    public string Category { get; private set; } = null!;
    public decimal BasePrice { get; private set; }
    public decimal? DiscountedPrice { get; private set; }
    public bool isActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public uint RowVersion { get; private set; }
    public Inventory? Inventory { get; private set; }
    private readonly List<Order> _orders = new();
    public IReadOnlyCollection<Order> Orders => _orders.AsReadOnly();
    private Product() { } // EF Core requires a parameterless constructor
    public static Product Create(string name, string description, string sku, string category, decimal basePrice)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvariantViolationException("Product name cannot be empty.");
        if (string.IsNullOrWhiteSpace(sku)) throw new InvariantViolationException("SKU cannot be empty.");
        if (basePrice <= 0) throw new InvariantViolationException("Base price must be positive.");

        return new Product
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Description = description.Trim(),
            SKU = sku.ToUpperInvariant().Trim(),
            Category = category,
            BasePrice = basePrice,
            isActive = true,
            CreatedAt = DateTime.UtcNow
        };
    }

    public decimal EffectivePrice => DiscountedPrice ?? BasePrice;

    public void ApplyDiscount(decimal discountedPrice)
    {
        if (discountedPrice <= 0 || discountedPrice >= BasePrice) throw new BusinessRuleException("INVALID_DISCOUNT", "Discounted price must be positive and less than the base price.");
        DiscountedPrice = discountedPrice;
        UpdatedAt = DateTime.UtcNow;

    }

    public void RemoveDiscount()
    {
        if (DiscountedPrice == null) throw new BusinessRuleException("NO_DISCOUNT", "No discount to remove.");
        DiscountedPrice = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateDetails(string name, string description, string category)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvariantViolationException("Product name cannot be empty.");
        Name = name.Trim();
        Description = description.Trim();
        Category = category;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        if (!isActive) throw new BusinessRuleException("ALREADY_INACTIVE", "Product is already inactive.");
        isActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        if (isActive) throw new BusinessRuleException("ALREADY_ACTIVE", "Product is already active.");
        isActive = true;
        UpdatedAt = DateTime.UtcNow;
    }


}

// INVENTORY
public class Inventory
{
    public Guid Id { get; private set;}
    public Guid ProductId { get; private set;}
    public int TotalStock { get; private set;}
    public int ReservedStock { get; private set;} // Locked by pending orders
    public int LowStockThreshold { get; private set;} // For notifications
    public DateTime LastUpdated { get; private set;}
    public uint RowVersion { get; private set;}
    public Product Product { get; private set;} = null!;
    public int AvailableStock => TotalStock - ReservedStock;
    public bool IsLowStock => AvailableStock <= LowStockThreshold;
    private Inventory() { } // EF Core requires a parameterless constructor
    public static Inventory Create(Guid productId, int initialStock, int lowStockThreshold = 10)
    {
        if (initialStock < 0) throw new InvariantViolationException("Initial stock cannot be negative.");
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

// reserve stock for an order. Throws if not enough available stock. Caller must handle releasing if order is cancelled or fails.
    public void Reserve(int quantity)
    {
        if (quantity <= 0) throw new InvariantViolationException("Quantity to reserve must be positive.");
        if(quantity > AvailableStock) throw new InsufficientInventoryException(ProductId, quantity, AvailableStock);
        ReservedStock += quantity;
        LastUpdated = DateTime.UtcNow;
    }

    //release reservation when order is cancelled or fails
    public void Release(int quantity)
    {
        
        if (quantity > ReservedStock) throw new InvariantViolationException($"Cannot release {quantity} units, only {ReservedStock} units are reserved.");
        ReservedStock -= quantity;
        LastUpdated = DateTime.UtcNow;
    }
    //commit reservation when order is completed. This reduces total stock and reserved stock.
    public void Commit(int quantity)
    {
        if (quantity > ReservedStock) throw new InvariantViolationException($"Cannot commit {quantity} units, only {ReservedStock} units are reserved.");
        ReservedStock -= quantity;
        TotalStock -= quantity;
        LastUpdated = DateTime.UtcNow;
    }

    public void Restock(int quantity)
    {
        if (quantity <= 0) throw new InvariantViolationException("Restock quantity must be positive.");
        TotalStock += quantity;
        LastUpdated = DateTime.UtcNow;
    }

    public void UpdateThreshold(int newThreshold)
    {
        if (newThreshold < 0) throw new InvariantViolationException("Low stock threshold cannot be negative.");
        LowStockThreshold = newThreshold;
        LastUpdated = DateTime.UtcNow;
    }
}

public class Order
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; } // Snapshot of price at order time
    public decimal TotalAmount { get; private set; }
    public OrderStatus Status { get; private set; }
    public string? CancellationReason { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public uint RowVersion { get; private set; }
    public User User { get; private set; } = null!;
    public Product Product { get; private set; } = null!;
    public Invoice? Invoice { get; private set; }
    private Order() { } // EF Core requires a parameterless constructor

    public static Order Create(Guid userId, Guid productId, int quantity, decimal unitPrice)
    {
        if (quantity <= 0) throw new InvariantViolationException("Order quantity must be positive.");
        if(unitPrice <= 0) throw new InvariantViolationException("Unit price must be positive");
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
        if(Status != OrderStatus.Pending) throw new BusinessRuleException("INVALID_ORDER_STATE", $"Cannot confirm payment for order in status {Status}.");
        Status = OrderStatus.Confirmed;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkShipped(){
        if(Status != OrderStatus.Confirmed) throw new BusinessRuleException("INVALID_ORDER_STATE", $"Cannot mark order as shipped in status {Status}.");
        Status = OrderStatus.Shipped;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Complete()
    {
        if(Status != OrderStatus.Shipped) throw new BusinessRuleException("INVALID_ORDER_STATE", $"Cannot complete order in status {Status}. Must be shipped first.");
        Status = OrderStatus.Completed;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Cancel(string reason)
    {
        if(Status == OrderStatus.Completed) throw new BusinessRuleException("INVALID_ORDER_STATE", "Cannot cancel a completed order.");
        if(Status == OrderStatus.Cancelled) throw new BusinessRuleException("INVALID_ORDER_STATE", "Order is already cancelled.");
        Status = OrderStatus.Cancelled;
        CancellationReason = reason;
        UpdatedAt = DateTime.UtcNow;
    }

}

//INVOICE
public class Invoice
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid OrderId { get; private set; }
    public string InvoiceNumber { get; private set; } = null!;
    public decimal Subtotal { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal TotalAmount { get; private set; }
    public decimal TaxRate { get; private set; }
    public InvoiceStatus Status { get; private set; }
    public DateTime IssuedAt { get; private set; }
    public DateTime? PaidAt { get; private set; }
    public DateTime? DueDate { get; private set; }
    public string? PaymentReference { get; private set; }
    public uint RowVersion { get; private set; }
    public User User { get; private set; } = null!;
    public Order Order { get; private set; } = null!;
    private Invoice() { } // EF Core requires a parameterless constructor
    public static Invoice Create(Guid userId, Guid orderId, decimal subtotal, decimal taxRate=0.20m)
    {
        if (subtotal <= 0) throw new InvariantViolationException("Subtotal must be positive.");
        if (taxRate < 0 || taxRate > 1) throw new InvariantViolationException("Tax rate must be between 0 and 1.");
        var taxAmount = Math.Round(subtotal * taxRate, 2);
        return new Invoice
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrderId = orderId,
            InvoiceNumber = GenerateInvoiceNumber(),
            Subtotal = subtotal,
            TaxRate = taxRate,
            TaxAmount = taxAmount,
            TotalAmount = subtotal + taxAmount,
            Status = InvoiceStatus.Pending,
            IssuedAt = DateTime.UtcNow,
            DueDate = DateTime.UtcNow.AddDays(30) // default payment terms
        };
    }
    
    public void MarkAsPaid(string paymentReference)
    {
        if(Status != InvoiceStatus.Pending) throw new InvalidInvoiceStateException(Status.ToString(), "Void");
        Status = InvoiceStatus.Void;
        PaymentReference = paymentReference;
        PaidAt = DateTime.UtcNow;
    }

    public void Void()
    {
        if(Status == InvoiceStatus.Paid) throw new InvalidInvoiceStateException(Status.ToString(), "Void");
        Status = InvoiceStatus.Void;
    }

    public bool IsOverdue => Status == InvoiceStatus.Pending && DateTime.UtcNow > DueDate;

    private static string GenerateInvoiceNumber() => $"INV-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..8].ToUpper()}";

}

//NOTIFICATION
public class Notification
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Channel { get; private set; } = null!;
    public string Subject { get; private set; } = null!;
    public string Body { get; private set; } = null!;
    public string Status { get; private set; } = null!;

    public int RetryCount { get; private set; }
    public string? FailureReason { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime? SentAt { get; private set; }
    public uint RowVersion { get; private set; }
    public User User { get; private set; } = null!;
    private Notification() { } // EF Core requires a parameterless constructor
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
        if(RetryCount >= 3)
        {
            throw new BusinessRuleException("MAX_RETRIES_EXCEEDED", "Notification has exceeded maximum retry attempts.");
        }
        Status = "Pending";
    
    }

}

//AUDIT LOG
public class AuditLog
{
    public Guid Id { get; private set; }
    public string EntityName { get; private set; } = null!;
    public string EntityId { get; private set; } = null!;
    public string Action { get; private set; } = null!; // e.g. "Created", "Updated", "Deleted"
    public string? OldValues { get; private set; } 

    public string? NewValues { get; private set; } 

    public Guid? UserId { get; private set; }

    public string? IpAddress { get; private set; }
    public DateTime Timestamp { get; private set; }
   

    private AuditLog() { } // EF Core requires a parameterless constructor
    public static AuditLog Create(string entityName, string entityId, string action, string? oldValues, string? newValues, Guid? userId, string? ipAddress = null)
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