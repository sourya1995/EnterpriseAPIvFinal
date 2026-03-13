using EnterpriseApi.API.Middleware;
using EnterpriseApi.Application.Extensions;
using EnterpriseApi.Infrastructure.Extensions;
using EnterpriseApi.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;

// ─────────────────────────────────────────────────────────────────────────────
// PROGRAM.CS — Composition Root
//
// This is the only place where all layers are wired together.
// It should be thin: delegate everything to extension methods.
// The order of middleware registration is critical and documented below.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// ─── Layer Registration (via ServiceCollectionExtensions) ──────────────────
// Each layer owns its own extension method — see:
//   EnterpriseApi.Application.Extensions.ServiceCollectionExtensions
//   EnterpriseApi.Infrastructure.Extensions.ServiceCollectionExtensions
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ─── MVC + Swagger ─────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Enterprise API",
        Version = "v1",
        Description = "Production-grade ASP.NET Core 8 API with Clean Architecture, " +
                     "Kafka, Background Services, and Concurrency patterns"
    });

    // Add JWT authentication to Swagger UI — allows testing protected endpoints
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization. Enter: 'Bearer {your-token}'",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                    { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// ─── JWT Authentication ────────────────────────────────────────────────────
var jwtSettings = builder.Configuration.GetSection("JwtSettings");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSettings["Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtSettings["Secret"]!)),
            // Only accept HMAC-SHA256 — prevents alg:none attacks
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            // 1 minute clock skew — compensates for server time drift in clustered deployments
            ClockSkew = TimeSpan.FromMinutes(1),
            // CRITICAL: MapInboundClaims = false prevents ASP.NET Core from remapping
            // "sub" to the long URI claim name, which breaks User.FindFirstValue("sub")
            NameClaimType = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub,
        };

        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = ctx =>
            {
                if (ctx.Exception is SecurityTokenExpiredException)
                    ctx.Response.Headers["X-Token-Expired"] = "true";
                return Task.CompletedTask;
            },
            OnChallenge = ctx =>
            {
                // Intercept the default 401 challenge and return ProblemDetails instead
                ctx.HandleResponse();
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/problem+json";
                return ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Status = 401,
                    Title = "Unauthorized",
                    Detail = "A valid JWT bearer token is required."
                });
            },
            OnForbidden = ctx =>
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.ContentType = "application/problem+json";
                return ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Status = 403,
                    Title = "Forbidden",
                    Detail = "You do not have the required role to access this resource."
                });
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole(c => c.FormatterName = "Simple");
    logging.AddDebug();
});

// ─── Build App ─────────────────────────────────────────────────────────────
var app = builder.Build();

// ─── Middleware Pipeline (ORDER IS CRITICAL) ────────────────────────────────
//
// 1. ExceptionHandling   — catches all exceptions from everything downstream
// 2. Swagger             — only in development
// 3. HTTPS Redirect      — before auth so redirects work cleanly
// 4. Authentication      — MUST come before Authorization
// 5. Authorization       — validates roles/policies on authenticated identity
// 6. MapControllers      — endpoint routing
//
// COMMON SENIOR MISTAKE: Putting Authorization before Authentication.
// HttpContext.User is not populated until UseAuthentication() runs.
// Authorization with an empty identity always fails → all requests return 401.

// 1. Catches exceptions thrown by any middleware below it in the pipeline
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Enterprise API v1");
        c.RoutePrefix = string.Empty; // Serve Swagger at root URL
    });
}

// 2. Redirect HTTP → HTTPS (before auth so the auth challenge uses HTTPS)
app.UseHttpsRedirection();

// 3. Identity population — reads JWT header, validates signature, populates HttpContext.User
app.UseAuthentication();

// 4. Role/policy checks — requires HttpContext.User to be populated by step 3
app.UseAuthorization();

app.MapControllers();

// ─── Database Seeding (Development Only) ──────────────────────────────────
if (app.Environment.IsDevelopment())
{
    await DatabaseSeeder.SeedAsync(app.Services);
}

app.Run();
