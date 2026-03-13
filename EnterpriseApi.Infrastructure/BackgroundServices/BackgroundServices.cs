using EnterpriseApi.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EnterpriseApi.Infrastructure.BackgroundServices;

/// <summary>
/// Notification Processor Background Service
///
/// Polls the database for pending notifications and "sends" them.
/// In production, this would call SendGrid, Twilio, or Firebase.
///
/// WHY A BACKGROUND SERVICE INSTEAD OF DIRECT SEND?
/// - HTTP requests return immediately (better latency)
/// - Retries are handled automatically
/// - Failed notifications don't fail the order flow
/// - Decoupled — swap notification provider without touching order logic
///
/// CONCURRENCY INSIGHT:
/// This service runs in its own thread. It must NOT use any Scoped services
/// captured from construction time (the Singleton trap). Instead, it creates
/// a new DI scope per batch using IServiceScopeFactory.
///
/// The SemaphoreSlim here prevents overlapping runs if processing takes
/// longer than the poll interval. Without it, you'd get two concurrent
/// notifications runs reading the same pending rows.
/// </summary>
public class NotificationProcessorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationProcessorService> _logger;

    // Prevents concurrent executions of the same background job
    private readonly SemaphoreSlim _processingLock = new(1, 1);

    private const int BatchSize = 50;
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(15);

    public NotificationProcessorService(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationProcessorService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NotificationProcessor started, polling every {Interval}s",
            PollingInterval.TotalSeconds);

        // Use PeriodicTimer (.NET 6+) — more efficient than Task.Delay loops
        // It doesn't drift: if processing takes 3s, the next tick is still 15s after the previous tick,
        // not 18s. Also handles cancellation cleanly.
        using var timer = new PeriodicTimer(PollingInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Try to acquire lock — if previous batch still processing, skip this tick
            if (!await _processingLock.WaitAsync(0, stoppingToken))
            {
                _logger.LogWarning("NotificationProcessor: previous batch still running, skipping tick");
                continue;
            }

            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Log but don't crash — service continues running
                _logger.LogError(ex, "NotificationProcessor: unhandled error in batch");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        _logger.LogInformation("NotificationProcessor stopped");
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        // Create a new scope per batch — gives us a fresh DbContext and UnitOfWork
        // CRITICAL: Background services are Singleton. DbContext is Scoped.
        // You MUST create a scope here. Direct injection of scoped services into
        // a Singleton causes the "captive dependency" bug.
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var pending = (await uow.Notifications.GetPendingAsync(BatchSize, ct)).ToList();

        if (!pending.Any())
        {
            _logger.LogDebug("NotificationProcessor: no pending notifications");
            return;
        }

        _logger.LogInformation("NotificationProcessor: processing {Count} notifications", pending.Count);

        // Process all notifications concurrently — use Parallel.ForEachAsync for .NET 6+
        // Max degree of parallelism prevents overwhelming the external provider
        await Parallel.ForEachAsync(pending,
            new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct },
            async (notification, innerCt) =>
            {
                try
                {
                    await SimulateSendAsync(notification.Channel, notification.Subject,
                        notification.Body, innerCt);

                    notification.MarkSent();
                    _logger.LogDebug("Notification {Id} sent via {Channel}", notification.Id, notification.Channel);
                }
                catch (Exception ex)
                {
                    notification.MarkFailed(ex.Message);
                    _logger.LogWarning(ex, "Notification {Id} failed (attempt {Retry})",
                        notification.Id, notification.RetryCount);
                }

                await uow.Notifications.UpdateAsync(notification, innerCt);
            });

        // Single SaveChanges for the entire batch — not per notification
        await uow.SaveChangesAsync(ct);

        _logger.LogInformation("NotificationProcessor: batch complete, {Sent} of {Total} sent",
            pending.Count(n => n.Status == "Sent"), pending.Count);
    }

    /// <summary>
    /// Simulates sending via different channels.
    /// In production: inject IEmailProvider, ISmsProvider etc.
    /// </summary>
    private static async Task SimulateSendAsync(string channel, string subject,
        string body, CancellationToken ct)
    {
        // Simulate network latency for different channels
        var delay = channel switch
        {
            "Email" => 100,
            "SMS" => 200,
            "Push" => 50,
            _ => 100
        };

        await Task.Delay(delay, ct);
        // Simulate occasional failures (5% failure rate for testing retry logic)
        if (Random.Shared.Next(100) < 5)
            throw new InvalidOperationException($"Simulated {channel} send failure");
    }
}

/// <summary>
/// Overdue Invoice Checker Background Service
///
/// Runs once per hour to find invoices past their due date.
/// Publishes notifications to warn users.
///
/// This demonstrates a SCHEDULED task pattern — not a real-time reactor.
/// For production, use Hangfire or Quartz.NET for more control
/// (retry on crash, execution history, distributed locks).
/// </summary>
public class OverdueInvoiceCheckerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OverdueInvoiceCheckerService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    public OverdueInvoiceCheckerService(
        IServiceScopeFactory scopeFactory,
        ILogger<OverdueInvoiceCheckerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OverdueInvoiceChecker started");

        using var timer = new PeriodicTimer(CheckInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckOverdueInvoicesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "OverdueInvoiceChecker: error during check");
            }
        }
    }

    private async Task CheckOverdueInvoicesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var invoiceService = scope.ServiceProvider.GetRequiredService<IInvoiceService>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var overdueInvoices = (await invoiceService.GetOverdueAsync(ct)).ToList();

        if (!overdueInvoices.Any())
        {
            _logger.LogDebug("OverdueInvoiceChecker: no overdue invoices found");
            return;
        }

        _logger.LogWarning("OverdueInvoiceChecker: found {Count} overdue invoices", overdueInvoices.Count);

        // Fire notifications for each overdue invoice — parallelized
        var tasks = overdueInvoices.Select(invoice =>
            notificationService.SendAsync(
                invoice.UserId, "Email",
                $"Invoice {invoice.InvoiceNumber} is Overdue",
                $"Invoice {invoice.InvoiceNumber} for £{invoice.TotalAmount:F2} " +
                $"was due on {invoice.DueDate:d}. Please arrange payment.",
                ct));

        await Task.WhenAll(tasks);
    }
}

/// <summary>
/// Inventory Alert Background Service
///
/// Runs every 30 minutes to check for low-stock products and emit events.
/// Complements the real-time Kafka events from OrderService with a safety net
/// in case the real-time event was missed or Kafka was down.
///
/// IDEMPOTENCY: This produces the same LowStock event if the condition persists.
/// Event consumers must handle duplicate events gracefully.
/// </summary>
public class InventoryMonitorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InventoryMonitorService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    // In-memory set of products already alerted this cycle — avoid duplicate alerts
    // ConcurrentDictionary used as a set for thread safety
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> _alreadyAlerted = new();

    public InventoryMonitorService(IServiceScopeFactory scopeFactory,
        ILogger<InventoryMonitorService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InventoryMonitor started");

        using var timer = new PeriodicTimer(CheckInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Clear alert cache each cycle — re-alert if still low after 30 min
            var oneHourAgo = DateTime.UtcNow.AddHours(-1);
            foreach (var key in _alreadyAlerted.Keys)
                if (_alreadyAlerted.TryGetValue(key, out var alertedAt) && alertedAt < oneHourAgo)
                    _alreadyAlerted.TryRemove(key, out _);

            await CheckInventoryAsync(stoppingToken);
        }
    }

    private async Task CheckInventoryAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var eventPublisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var lowStockItems = (await uow.Inventories.GetLowStockAsync(ct)).ToList();

        foreach (var inv in lowStockItems)
        {
            if (_alreadyAlerted.ContainsKey(inv.ProductId)) continue;

            _alreadyAlerted.TryAdd(inv.ProductId, DateTime.UtcNow);

            await eventPublisher.PublishAsync(new Domain.Events.InventoryLowStockEvent
            {
                ProductId = inv.ProductId,
                ProductName = inv.Product?.Name ?? "Unknown",
                CurrentStock = inv.AvailableStock,
                ThresholdLevel = inv.LowStockThreshold
            }, ct);

            _logger.LogWarning("Low stock alert: Product {ProductId} has {Stock} units",
                inv.ProductId, inv.AvailableStock);
        }
    }
}
