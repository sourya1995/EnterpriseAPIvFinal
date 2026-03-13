using EnterpriseApi.Application.Interfaces;
using EnterpriseApi.Domain.Entities;
using EnterpriseApi.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace EnterpriseApi.Infrastructure.Services;

// ─────────────────────────────────────────────────────────────────────────────
// TOKEN SERVICE
// ─────────────────────────────────────────────────────────────────────────────
public class TokenService : ITokenService
{
    private readonly IConfiguration _config;
    private readonly ILogger<TokenService> _logger;

    public TokenService(IConfiguration config, ILogger<TokenService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public string GenerateToken(User user)
    {
        var jwt = _config.GetSection("JwtSettings");
        var secret = jwt["Secret"] ?? throw new InvalidOperationException("JWT Secret not configured.");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("firstName", user.FirstName),
            new Claim("lastName", user.LastName),
        };

        var expiry = DateTime.UtcNow.AddMinutes(
            int.Parse(jwt["ExpiryMinutes"] ?? "60"));

        var token = new JwtSecurityToken(
            issuer: jwt["Issuer"],
            audience: jwt["Audience"],
            claims: claims,
            expires: expiry,
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var jwt = _config.GetSection("JwtSettings");
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Secret"]!));
            var handler = new JwtSecurityTokenHandler();

            return handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt["Issuer"],
                ValidateAudience = true,
                ValidAudience = jwt["Audience"],
                ValidateLifetime = true,
                IssuerSigningKey = key,
                ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                ClockSkew = TimeSpan.FromMinutes(1)
            }, out _);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Token validation failed: {Message}", ex.Message);
            return null;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CURRENT USER SERVICE
// Wraps IHttpContextAccessor — allows services to access the current user's
// identity without depending on HttpContext directly.
// Registered as Scoped — safe because IHttpContextAccessor is per-request.
// ─────────────────────────────────────────────────────────────────────────────
public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;

    public Guid? UserId
    {
        get
        {
            var sub = User?.FindFirstValue(JwtRegisteredClaimNames.Sub)
                   ?? User?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(sub, out var id) ? id : null;
        }
    }

    public string? UserEmail => User?.FindFirstValue(JwtRegisteredClaimNames.Email)
                             ?? User?.FindFirstValue(ClaimTypes.Email);

    public string? UserRole => User?.FindFirstValue(ClaimTypes.Role);

    public string? IpAddress => _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;
}

// ─────────────────────────────────────────────────────────────────────────────
// DATABASE SEEDER
// Provides initial data for development/testing — admin user and sample products.
// ─────────────────────────────────────────────────────────────────────────────
public static class DatabaseSeeder
{
    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

        await db.Database.EnsureCreatedAsync();

        // Idempotent — only seeds if data doesn't exist
        if (await db.Users.AnyAsync()) return;

        logger.LogInformation("Seeding database...");

        // Admin user
        var adminHash = hasher.HashPassword(null!, "Admin@1234!");
        var admin = User.Create("admin@enterprise.com", adminHash, "System", "Admin", "Admin");
        db.Users.Add(admin);

        // Manager user
        var managerHash = hasher.HashPassword(null!, "Manager@1234!");
        var manager = User.Create("manager@enterprise.com", managerHash, "Jane", "Manager", "Manager");
        db.Users.Add(manager);

        // Regular user
        var userHash = hasher.HashPassword(null!, "User@1234!");
        var regularUser = User.Create("user@enterprise.com", userHash, "John", "Doe", "User");
        db.Users.Add(regularUser);

        // Products
        var products = new[]
        {
            ("Laptop Pro 15", "High-performance laptop for developers", "LAPTOP-PRO-15", "Electronics", 1299.99m, 50, 5),
            ("Mechanical Keyboard", "Cherry MX Blue switches, RGB", "KB-MX-RGB-01", "Electronics", 149.99m, 200, 20),
            ("4K Monitor 27\"", "IPS panel, 144Hz refresh rate", "MON-4K-27-IPS", "Electronics", 549.99m, 30, 5),
            ("Ergonomic Mouse", "Wireless, vertical design", "MOUSE-ERG-WL", "Electronics", 69.99m, 150, 15),
            ("Standing Desk", "Electric height adjustment", "DESK-ELEC-SIT-STAND", "Furniture", 799.99m, 20, 3),
            ("Office Chair", "Lumbar support, adjustable arms", "CHAIR-ERG-PRO", "Furniture", 449.99m, 25, 5),
        };

        foreach (var (name, desc, sku, cat, price, stock, threshold) in products)
        {
            var product = Product.Create(name, desc, sku, cat, price);
            db.Products.Add(product);

            var inventory = Inventory.Create(product.Id, stock, threshold);
            db.Inventories.Add(inventory);
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Database seeded successfully");
    }
}
