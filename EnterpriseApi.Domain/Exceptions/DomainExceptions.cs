namespace EnterpriseApi.Domain.Exceptions;

// ─── Base Domain Exception ────────────────────────────────────────────────────
// All domain exceptions derive from this. The middleware maps these to HTTP responses.
// Using a custom hierarchy (vs generic Exception) lets the middleware pattern-match precisely.

public abstract class DomainException : Exception
{
    public string Code { get; }

    protected DomainException(string code, string message) : base(message)
    {
        Code = code;
    }

    protected DomainException(string code, string message, Exception inner)
        : base(message, inner)
    {
        Code = code;
    }
}

// ─── 404 Not Found ────────────────────────────────────────────────────────────
public class NotFoundException : DomainException
{
    public NotFoundException(string entity, object key)
        : base("NOT_FOUND", $"{entity} with identifier '{key}' was not found.") { }
}

// ─── 400 Business Rule Violations ────────────────────────────────────────────
public class BusinessRuleException : DomainException
{
    public BusinessRuleException(string code, string message)
        : base(code, message) { }
}

// ─── 401 Unauthorized ────────────────────────────────────────────────────────
public class UnauthorizedException : DomainException
{
    public UnauthorizedException(string message = "Authentication required.")
        : base("UNAUTHORIZED", message) { }
}

// ─── 403 Forbidden ───────────────────────────────────────────────────────────
public class ForbiddenException : DomainException
{
    public ForbiddenException(string message = "You do not have permission to perform this action.")
        : base("FORBIDDEN", message) { }
}

// ─── 409 Concurrency Conflict ────────────────────────────────────────────────
// Thrown when optimistic concurrency detects a stale update attempt.
// Caller should re-read the entity and retry or surface the conflict to the user.
public class ConcurrencyConflictException : DomainException
{
    public string EntityName { get; }
    public object EntityId { get; }

    public ConcurrencyConflictException(string entityName, object entityId)
        : base("CONCURRENCY_CONFLICT",
               $"{entityName} '{entityId}' was modified by another process. Please retry.")
    {
        EntityName = entityName;
        EntityId = entityId;
    }
}

// ─── 422 Invariant Violations ────────────────────────────────────────────────
public class InvariantViolationException : DomainException
{
    public InvariantViolationException(string message)
        : base("INVARIANT_VIOLATION", message) { }
}

// ─── 409 Duplicate / Conflict ────────────────────────────────────────────────
public class DuplicateException : DomainException
{
    public DuplicateException(string entity, string field, object value)
        : base("DUPLICATE", $"{entity} with {field} '{value}' already exists.") { }
}

// ─── Inventory Specific ───────────────────────────────────────────────────────
public class InsufficientInventoryException : DomainException
{
    public Guid ProductId { get; }
    public int Requested { get; }
    public int Available { get; }

    public InsufficientInventoryException(Guid productId, int requested, int available)
        : base("INSUFFICIENT_INVENTORY",
               $"Product '{productId}' has only {available} units available. Requested: {requested}.")
    {
        ProductId = productId;
        Requested = requested;
        Available = available;
    }
}

// ─── Invoice Specific ────────────────────────────────────────────────────────
public class InvalidInvoiceStateException : DomainException
{
    public InvalidInvoiceStateException(string current, string attempted)
        : base("INVALID_INVOICE_STATE",
               $"Cannot transition invoice from '{current}' to '{attempted}'.") { }
}

// ─── Payment Specific ─────────────────────────────────────────────────────────
public class PaymentFailedException : DomainException
{
    public string Reason { get; }

    public PaymentFailedException(string reason)
        : base("PAYMENT_FAILED", $"Payment processing failed: {reason}")
    {
        Reason = reason;
    }
}

// ─── Messaging / Infrastructure ───────────────────────────────────────────────
// These bubble up from Kafka producer failures — caught by global handler.
public class MessagePublishException : DomainException
{
    public string Topic { get; }

    public MessagePublishException(string topic, string message, Exception? inner = null)
        : base("MESSAGE_PUBLISH_FAILED", message, inner ?? new Exception(message))
    {
        Topic = topic;
    }
}
