namespace EnterpriseApi.Domain.Exceptions;

//base domain exception class, all domain exceptions should inherit from this class

public abstract class DomainException : Exception
{
    public string Code { get; }
    protected DomainException(string code, string message): base(message)
    {
        Code = code;
    
    }

    protected DomainException(string code, string message, Exception inner): base(message, inner)
    {
        Code = code;
    }
}

// 404 not found exception
public class NotFoundException: DomainException
{
    public NotFoundException(string entity, object key): base("not_found", $"{entity} with key {key} was not found.")
    {
    }
}

// 400 Business rule violation exception
public class BusinessRuleException: DomainException
{
    public BusinessRuleException(string code, string message): base(code, message){}
}

// 401 unauthorized exception
public class UnauthorizedException: DomainException
{
    public UnauthorizedException(string message = "Authentication required."): base("UNAUTHORIZED", message){}
}

// 403 forbidden exception
public class ForbiddenException: DomainException
{
    public ForbiddenException(string message= "You do not have permission to perform this action."): base("FORBIDDEN", message){}
}

// 409 conflict exception
public class ConcurrencyConflictException: DomainException
{
    public string EntityName { get; }
    public object EntityId { get; }
    public ConcurrencyConflictException(string entityName, object entityId): base("CONCURRENCY_CONFLICT", $"The {entityName} with ID {entityId} was modified by another process. Please reload and try again.")
    {
        EntityName = entityName;
        EntityId = entityId;
    }
}

// 422 Invariant violation exception
public class InvariantViolationException: DomainException
{
    public InvariantViolationException(string message): base("INVARIANT_VIOLATION", message){}
}

// 409 Duplicate/Conflict
public class DuplicateException: DomainException
{
    public DuplicateException(string entity, string field, object value): base("DUPLICATE", $"{entity} with {field} = {value} already exists.")
    {
    }
}

// 429 Too many requests
public class TooManyRequestsException: DomainException
{
    public TooManyRequestsException(string message = "Too many requests. Please try again later."): base("TOO_MANY_REQUESTS", message){}
}

// Inventory specific exceptions
public class InsufficientInventoryException: DomainException
{
    public Guid ProductId { get; }
    public int RequestedQuantity { get; }
    public int AvailableQuantity { get; }
    public InsufficientInventoryException(Guid productId, int requestedQuantity, int availableQuantity): base("INSUFFICIENT_INVENTORY", $"Insufficient inventory for product {productId}. Requested: {requestedQuantity}, Available: {availableQuantity}.")
    {
        ProductId = productId;
        RequestedQuantity = requestedQuantity;
        AvailableQuantity = availableQuantity;
    }
}

//Invoice specific exceptions
public class InvalidInvoiceStateException: DomainException
{
    public InvalidInvoiceStateException(string current, string attempted): base("INVALID_INVOICE_STATE", $"Invalid invoice state. Cannot transition from current: {current}, to attempted: {attempted}."){}
}

//Payment specific exceptions
public class PaymentFailedException: DomainException
{
   public string Reason { get; }
   public PaymentFailedException(string reason): base("PAYMENT_FAILED", $"Payment failed. Reason: {reason}.")
   {
       Reason = reason;
   }
}


//Messaging specific exceptions
public class MessagePublishException: DomainException
{
    public string Topic { get; }
    public MessagePublishException(string topic, string message, Exception? inner = null): base("MESSAGE_PUBLISH_FAILED", message, inner ?? new Exception(message))
    {
        Topic = topic;
    }
}