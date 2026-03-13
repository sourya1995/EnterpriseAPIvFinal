namespace EnterpriseApi.Domain.Enums;

public enum OrderStatus
{
    Pending,
    Confirmed,
    Shipped,
    Completed,
    Cancelled
}

public enum InvoiceStatus
{
    Pending,
    Paid,
    Void
}

public enum NotificationChannel
{
    Email,
    SMS,
    Push
}
