using EnterpriseApi.Application.DTOs;
using EnterpriseApi.Application.Interfaces;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace EnterpriseApi.API.Controllers;

// ─────────────────────────────────────────────────────────────────────────────
// AUTH CONTROLLER
// ─────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("api/v1/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly IValidator<RegisterRequest> _registerValidator;

    public AuthController(IAuthService authService,
        IValidator<LoginRequest> loginValidator,
        IValidator<RegisterRequest> registerValidator)
    {
        _authService = authService;
        _loginValidator = loginValidator;
        _registerValidator = registerValidator;
    }

    /// <summary>Authenticate with email and password. Returns a JWT token.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request,
        CancellationToken ct)
    {
        var validation = await _loginValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return BadRequest(new ValidationProblemDetails(validation.ToDictionary()));

        var result = await _authService.LoginAsync(request, ct);
        return Ok(result);
    }

    /// <summary>Register a new user account.</summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponse), 201)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request,
        CancellationToken ct)
    {
        var validation = await _registerValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return BadRequest(new ValidationProblemDetails(validation.ToDictionary()));

        var result = await _authService.RegisterAsync(request, ct);
        return StatusCode(201, result);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// USERS CONTROLLER
// ─────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly IValidator<CreateUserRequest> _createValidator;

    public UsersController(IUserService userService,
        IValidator<CreateUserRequest> createValidator)
    {
        _userService = userService;
        _createValidator = createValidator;
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(PagedResult<UserDto>), 200)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
        => Ok(await _userService.GetAllAsync(page, pageSize, ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(UserDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        var currentRole = GetCurrentRole();

        if (currentRole != "Admin" && currentUserId != id)
            return Forbid();

        return Ok(await _userService.GetByIdAsync(id, ct));
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(UserDto), 201)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request,
        CancellationToken ct)
    {
        var validation = await _createValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return BadRequest(new ValidationProblemDetails(validation.ToDictionary()));

        var result = await _userService.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(UserDto), 200)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest request,
        CancellationToken ct)
    {
        if (GetCurrentRole() != "Admin" && GetCurrentUserId() != id)
            return Forbid();

        return Ok(await _userService.UpdateAsync(id, request, ct));
    }

    [HttpPatch("{id:guid}/role")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(UserDto), 200)]
    public async Task<IActionResult> ChangeRole(Guid id, [FromBody] ChangeRoleRequest request,
        CancellationToken ct)
        => Ok(await _userService.ChangeRoleAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        await _userService.DeactivateAsync(id, ct);
        return NoContent();
    }

    [HttpPatch("{id:guid}/activate")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        await _userService.ActivateAsync(id, ct);
        return NoContent();
    }

    private Guid? GetCurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
               ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private string? GetCurrentRole() => User.FindFirstValue(ClaimTypes.Role);
}

// ─────────────────────────────────────────────────────────────────────────────
// PRODUCTS CONTROLLER
// ─────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("api/v1/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly IProductService _productService;
    private readonly IValidator<CreateProductRequest> _createValidator;
    private readonly IValidator<RestockRequest> _restockValidator;
    private readonly IValidator<ApplyDiscountRequest> _discountValidator;

    public ProductsController(IProductService productService,
        IValidator<CreateProductRequest> createValidator,
        IValidator<RestockRequest> restockValidator,
        IValidator<ApplyDiscountRequest> discountValidator)
    {
        _productService = productService;
        _createValidator = createValidator;
        _restockValidator = restockValidator;
        _discountValidator = discountValidator;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ProductDto>), 200)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? category = null,
        CancellationToken ct = default)
        => Ok(await _productService.GetAllAsync(page, pageSize, category, ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ProductDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
        => Ok(await _productService.GetByIdAsync(id, ct));

    [HttpPost]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(ProductDto), 201)]
    public async Task<IActionResult> Create([FromBody] CreateProductRequest request,
        CancellationToken ct)
    {
        var validation = await _createValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return BadRequest(new ValidationProblemDetails(validation.ToDictionary()));

        var result = await _productService.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(ProductDto), 200)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateProductRequest request,
        CancellationToken ct)
        => Ok(await _productService.UpdateAsync(id, request, ct));

    [HttpPatch("{id:guid}/discount")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(ProductDto), 200)]
    public async Task<IActionResult> ApplyDiscount(Guid id,
        [FromBody] ApplyDiscountRequest request, CancellationToken ct)
    {
        var validation = await _discountValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return BadRequest(new ValidationProblemDetails(validation.ToDictionary()));

        return Ok(await _productService.ApplyDiscountAsync(id, request, ct));
    }

    [HttpPatch("{id:guid}/restock")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(ProductDto), 200)]
    public async Task<IActionResult> Restock(Guid id, [FromBody] RestockRequest request,
        CancellationToken ct)
    {
        var validation = await _restockValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return BadRequest(new ValidationProblemDetails(validation.ToDictionary()));

        return Ok(await _productService.RestockAsync(id, request, ct));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        await _productService.DeactivateAsync(id, ct);
        return NoContent();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ORDERS CONTROLLER
// ─────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;
    private readonly IValidator<CreateOrderRequest> _createValidator;
    private readonly IValidator<CancelOrderRequest> _cancelValidator;

    public OrdersController(IOrderService orderService,
        IValidator<CreateOrderRequest> createValidator,
        IValidator<CancelOrderRequest> cancelValidator)
    {
        _orderService = orderService;
        _createValidator = createValidator;
        _cancelValidator = cancelValidator;
    }

    [HttpGet]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(PagedResult<OrderDto>), 200)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
        => Ok(await _orderService.GetAllAsync(page, pageSize, status, ct));

    [HttpGet("my")]
    [ProducesResponseType(typeof(PagedResult<OrderDto>), 200)]
    public async Task<IActionResult> GetMyOrders(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();
        return Ok(await _orderService.GetByUserIdAsync(userId.Value, page, pageSize, ct));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrderDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
        => Ok(await _orderService.GetByIdAsync(id, ct));

    [HttpPost]
    [ProducesResponseType(typeof(OrderDto), 201)]
    [ProducesResponseType(typeof(ProblemDetails), 409)] // InsufficientInventory
    public async Task<IActionResult> Create([FromBody] CreateOrderRequest request,
        CancellationToken ct)
    {
        var validation = await _createValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return BadRequest(new ValidationProblemDetails(validation.ToDictionary()));

        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var result = await _orderService.CreateOrderAsync(userId.Value, request, ct);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpPatch("{id:guid}/cancel")]
    [ProducesResponseType(typeof(OrderDto), 200)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelOrderRequest request,
        CancellationToken ct)
    {
        var validation = await _cancelValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return BadRequest(new ValidationProblemDetails(validation.ToDictionary()));

        return Ok(await _orderService.CancelOrderAsync(id, request, ct));
    }

    [HttpPatch("{id:guid}/ship")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(OrderDto), 200)]
    public async Task<IActionResult> Ship(Guid id, CancellationToken ct)
        => Ok(await _orderService.ShipOrderAsync(id, ct));

    [HttpPatch("{id:guid}/complete")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(OrderDto), 200)]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct)
        => Ok(await _orderService.CompleteOrderAsync(id, ct));

    private Guid? GetCurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
               ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// INVOICES CONTROLLER
// ─────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class InvoicesController : ControllerBase
{
    private readonly IInvoiceService _invoiceService;
    private readonly IValidator<PayInvoiceRequest> _payValidator;

    public InvoicesController(IInvoiceService invoiceService,
        IValidator<PayInvoiceRequest> payValidator)
    {
        _invoiceService = invoiceService;
        _payValidator = payValidator;
    }

    [HttpGet("overdue")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(IEnumerable<InvoiceDto>), 200)]
    public async Task<IActionResult> GetOverdue(CancellationToken ct)
        => Ok(await _invoiceService.GetOverdueAsync(ct));

    [HttpGet("my")]
    [ProducesResponseType(typeof(PagedResult<InvoiceDto>), 200)]
    public async Task<IActionResult> GetMyInvoices(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();
        return Ok(await _invoiceService.GetByUserIdAsync(userId.Value, page, pageSize, ct));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(InvoiceDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
        => Ok(await _invoiceService.GetByIdAsync(id, ct));

    [HttpGet("order/{orderId:guid}")]
    [ProducesResponseType(typeof(InvoiceDto), 200)]
    public async Task<IActionResult> GetByOrder(Guid orderId, CancellationToken ct)
        => Ok(await _invoiceService.GetByOrderIdAsync(orderId, ct));

    [HttpPatch("{id:guid}/pay")]
    [ProducesResponseType(typeof(InvoiceDto), 200)]
    public async Task<IActionResult> Pay(Guid id, [FromBody] PayInvoiceRequest request,
        CancellationToken ct)
    {
        var validation = await _payValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return BadRequest(new ValidationProblemDetails(validation.ToDictionary()));

        return Ok(await _invoiceService.PayInvoiceAsync(id, request, ct));
    }

    private Guid? GetCurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
               ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// NOTIFICATIONS CONTROLLER
// ─────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;

    public NotificationsController(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    [HttpGet("my")]
    [ProducesResponseType(typeof(PagedResult<NotificationDto>), 200)]
    public async Task<IActionResult> GetMyNotifications(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();
        return Ok(await _notificationService.GetByUserIdAsync(userId.Value, page, pageSize, ct));
    }

    private Guid? GetCurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
               ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
