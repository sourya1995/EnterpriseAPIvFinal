using EnterpriseApi.Application.DTOs;
using FluentValidation;

namespace EnterpriseApi.Application.Validators;

public class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email)
        .NotEmpty().EmailAddress().MaximumLength(255);

        RuleFor(x => x.Password)
        .NotEmpty().MinimumLength(8)
        .Matches("[A-Z]").WithMessage("Password must contain at least one uppercase letter")
        .Matches("[0-9]").WithMessage("Password must contain at least one digit")
        .Matches("[^a-zA-Z0-9]").WithMessage("Password must contain at least one special character");

        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Role).NotEmpty().Must(r => r is "Admin" or "User" or "Manager").WithMessage("Role must be either 'Admin' or 'User' or 'Manager'. ");
    }
}

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
        .NotEmpty().EmailAddress();

        RuleFor(x => x.Password)
        .NotEmpty();
    }
}

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email)
        .NotEmpty().EmailAddress().MaximumLength(255);

        RuleFor(x => x.Password)
        .NotEmpty().MinimumLength(8)
        .Matches("[A-Z]").WithMessage("Password must contain at least one uppercase letter")
        .Matches("[0-9]").WithMessage("Password must contain at least one digit");

        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
    }
}

public class CreateProductRequestValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.SKU).NotEmpty().MaximumLength(50).Matches("^[A-Z0-9\\-]+$").WithMessage("SKU must be alphanumeric and contain dashes");
        RuleFor(x => x.Category).NotEmpty().MaximumLength(100);
        RuleFor(x => x.BasePrice).GreaterThan(0).LessThanOrEqualTo(1_000_000);
        RuleFor(x => x.InitialStock).GreaterThanOrEqualTo(0);
        RuleFor(x => x.LowStockThreshold).GreaterThanOrEqualTo(0);
    }
}

public class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderRequestValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(1000);
    }
}

public class ApplyDiscountRequestValidator : AbstractValidator<ApplyDiscountRequest>
{
    public ApplyDiscountRequestValidator()
    {
        RuleFor(x => x.DiscountedPrice).GreaterThan(0);
    }
}

public class RestockRequestValidator : AbstractValidator<RestockRequest>
{
    public RestockRequestValidator()
    {
        RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(100_000);
    }
}

public class CancelOrderRequestValidator : AbstractValidator<CancelOrderRequest>
{
    public CancelOrderRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public class PayInvoiceRequestValidator : AbstractValidator<PayInvoiceRequest>
{
    public PayInvoiceRequestValidator()
    {
        RuleFor(x => x.PaymentReference).NotEmpty().MaximumLength(100);
    }
}