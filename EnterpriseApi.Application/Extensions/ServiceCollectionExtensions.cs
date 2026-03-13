using EnterpriseApi.Application.Services;
using EnterpriseApi.Application.Interfaces;
using EnterpriseApi.Application.Validators;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseApi.Application.Extensions;

/// <summary>
/// Extension method pattern for clean DI registration.
/// Each layer owns its own ServiceCollectionExtensions — the API layer
/// simply calls builder.Services.AddApplication() and never knows the internals.
/// This is the "composition root" principle: one place configures all dependencies.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // ─── Application Services (Scoped = per HTTP request) ──────────────────
        // Scoped is correct here because services coordinate a single unit of work
        // per request. Making them Singleton would mean they'd share IUnitOfWork
        // across requests — a concurrency disaster with DbContext.
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IInvoiceService, InvoiceService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IAuditService, AuditService>();

        // ─── FluentValidation ──────────────────────────────────────────────────
        // Scans the Application assembly and registers all AbstractValidator<T> implementations.
        // Usage in controllers: inject IValidator<CreateOrderRequest> directly.
        services.AddValidatorsFromAssemblyContaining<CreateUserRequestValidator>();

        return services;
    }
}
