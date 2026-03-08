namespace EnterpriseApi.Domain.Events;

//Base Domain Event
// Domain events represent facts that happened in the domain.
// They are serialized and published to Kafka topic
// EventType string is used as Kafka message key for routing

public abstract record DomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; init;} = DateTime.UtcNow;
    public abstract string EventType { get; }
    public string Version {get; init; } = "1.0";
}

// Order Event
public record OrderCreatedEvent: DomainEvent
{
    public override string EventType => "order.created";
    public required Guid OrderId { get; init; }
    public required Guid UserId { get; init; }
    public required Guid ProductId { get; init; }
    public required int Quantity { get; init; }
    public required decimal TotalAmount { get; init; }
    public required string ProductName { get; init; }

}

public record OrderCancelledEvent: DomainEvent
{
    public override string EventType => "order.cancelled";
    public required Guid OrderId { get; init; }
    public required Guid UserId { get; init; }
    public required Guid ProductId { get; init; }
    public required int Quantity { get; init; }
    public string? CancellationReason { get; init; }
}

public record OrderCompletedEvent: DomainEvent
{
    public override string EventType => "order.completed";
    public required Guid OrderId { get; init; }
    public required Guid UserId {get; init;}
    public required Guid InvoiceId { get; init; }
    public required decimal Amount { get; init; }
}

//Inventory Events
public record InventoryReservedEvent: DomainEvent
{
    public override string EventType => "inventory.reserved";
    public required Guid ProductId { get; init; }
    public required Guid OrderId { get; init; }
    public required int QuantityReserved { get; init; }
    public required int QuantityRemaining { get; init; }
}


public record InventoryLowStockEvent: DomainEvent
{
    public override string EventType => "inventory.low_stock";
    public required Guid ProductId { get; init; }
    public required string ProductName { get; init; }
    public required int CurrentStock { get; init; }
    public required int ThresholdLevel { get; init; }
}

public record InventoryRestockedEvent: DomainEvent
{
    public override string EventType => "inventory.restocked";
    public required Guid ProductId { get; init; }
    public required int QuantityAdded { get; init; }
    public required int NewTotalStock { get; init; }
}

// Invoice Events
public record InvoiceGeneratedEvent: DomainEvent
{
    public override string EventType => "invoice.generated";
    public required Guid InvoiceId { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid UserId { get; init; }
    public required decimal Amount { get; init; }
   
}

public record InvoicePaidEvent: DomainEvent
{
    public override string EventType => "invoice.paid";
    public required Guid InvoiceId { get; init; }
    public required Guid UserId { get; init; }
    public required decimal AmountPaid { get; init; }
}

// Notification Events
public record NotificationRequestedEvent: DomainEvent
{
    public override string EventType => "notification.requested";
    public required Guid UserId { get; init; }
    public required string Channel { get; init; } // e.g., "email", "sms"
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public string? TemplateId { get; init; }
}

// User Events
public record UserRegisteredEvent: DomainEvent
{
    public override string EventType => "user.registered";
    public required Guid UserId { get; init; }
    public required string Email { get; init; }
    public required string FirstName { get; init; }
}


