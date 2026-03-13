using EnterpriseApi.Domain.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace EnterpriseApi.API.Middleware;

/// <summary>
/// Global exception handler — must be the FIRST middleware in the pipeline.
/// Converts all domain and infrastructure exceptions into RFC 7807 ProblemDetails responses.
///
/// WHY CENTRALIZE THIS?
/// Without it, each controller catches exceptions with try/catch, creating inconsistent
/// error shapes (some return {error: "..."}, others return {message: "..."}, etc.).
/// Frontend clients must handle N different shapes. With centralized handling,
/// every error follows the same ProblemDetails contract.
///
/// PRODUCTION PRINCIPLE: Never expose stack traces. Log full details server-side,
/// return only a traceId to the client so support can correlate logs.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    // Singleton-safe: no mutable state, ILogger is thread-safe
    public ExceptionHandlingMiddleware(RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;

        var (statusCode, title, detail) = exception switch
        {
            NotFoundException ex
                => (StatusCodes.Status404NotFound, "Resource Not Found", ex.Message),

            InsufficientInventoryException ex
                => (StatusCodes.Status409Conflict, "Inventory Conflict",
                    $"Requested {ex.Requested} units but only {ex.Available} available."),

            ConcurrencyConflictException ex
                => (StatusCodes.Status409Conflict, "Concurrency Conflict", ex.Message),

            DuplicateException ex
                => (StatusCodes.Status409Conflict, "Duplicate Resource", ex.Message),

            BusinessRuleException ex
                => (StatusCodes.Status422UnprocessableEntity, "Business Rule Violation", ex.Message),

            InvariantViolationException ex
                => (StatusCodes.Status400BadRequest, "Domain Invariant Violation", ex.Message),

            InvalidInvoiceStateException ex
                => (StatusCodes.Status422UnprocessableEntity, "Invalid Invoice State", ex.Message),

            PaymentFailedException ex
                => (StatusCodes.Status402PaymentRequired, "Payment Failed", ex.Message),

            UnauthorizedException ex
                => (StatusCodes.Status401Unauthorized, "Unauthorized", ex.Message),

            ForbiddenException ex
                => (StatusCodes.Status403Forbidden, "Forbidden", ex.Message),

            MessagePublishException ex
                => (StatusCodes.Status503ServiceUnavailable, "Messaging Error",
                    "An error occurred publishing the event. The operation may need to be retried."),

            ValidationException ex
                => (StatusCodes.Status400BadRequest, "Validation Failed",
                    string.Join("; ", ex.Errors.Select(e => e.ErrorMessage))),

            OperationCanceledException
                => (499, "Request Cancelled", "The client cancelled the request."),

            _ => (StatusCodes.Status500InternalServerError, "Internal Server Error",
                  "An unexpected error occurred. Please contact support with the trace ID.")
        };

        // Only log full exception for unhandled 500s — domain exceptions are expected
        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception,
                "Unhandled exception. TraceId={TraceId} Path={Path}",
                traceId, context.Request.Path);
        }
        else
        {
            _logger.LogWarning(
                "Handled exception {ExceptionType}: {Message}. TraceId={TraceId}",
                exception.GetType().Name, exception.Message, traceId);
        }

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        };

        // Include error code from DomainException hierarchy for client-side handling
        if (exception is DomainException domainEx)
            problem.Extensions["errorCode"] = domainEx.Code;

        problem.Extensions["traceId"] = traceId;
        problem.Extensions["timestamp"] = DateTime.UtcNow.ToString("O");

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(problem);
    }
}
