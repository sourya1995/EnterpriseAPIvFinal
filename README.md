# EnterpriseApi — .NET 8 Clean Architecture API

A production-grade REST API built with ASP.NET Core 8, demonstrating Clean Architecture, Domain-Driven Design, Kafka event streaming, multi-level caching, rate limiting, distributed tracing, and resilience patterns.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Project Structure](#2-project-structure)
3. [Architecture Overview](#3-architecture-overview)
4. [Step-by-Step Local Setup (No Docker)](#4-step-by-step-local-setup-no-docker)
5. [Step-by-Step Local Setup (With Kafka)](#5-step-by-step-local-setup-with-kafka)
6. [Running the Application](#6-running-the-application)
7. [Exploring the API](#7-exploring-the-api)
8. [API Reference](#8-api-reference)
9. [Configuration Reference](#9-configuration-reference)
10. [Adding the Billion-User Enhancements](#10-adding-the-billion-user-enhancements)
11. [Project Layer Guide](#11-project-layer-guide)
12. [Common Issues and Fixes](#12-common-issues-and-fixes)

---

## 1. Prerequisites

Install these before starting. Verify each with the command shown.

| Tool | Minimum Version | Verify |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | 8.0 | `dotnet --version` |
| [Git](https://git-scm.com/) | any | `git --version` |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | 4.x | `docker --version` (only needed for Kafka) |
| A REST client | any | [Swagger UI](http://localhost:5267) works out of the box |

**Windows note:** All commands below use the Windows path separator `\`. On macOS/Linux, replace `\` with `/`.

---

## 2. Project Structure

```
EnterpriseApi/                          ← solution root
├── EnterpriseApi.sln
├── docker-compose.yml                  ← Kafka + Zookeeper + Kafka UI
│
├── EnterpriseApi.Domain/               ← Layer 1: pure business rules, no dependencies
│   ├── Entities/Entities.cs            ← User, Product, Inventory, Order, Invoice, Notification, AuditLog
│   ├── Enums/Enums.cs                  ← OrderStatus, UserRole, NotificationChannel, etc.
│   ├── Events/DomainEvents.cs          ← OrderCreated, InventoryReserved, UserRegistered, etc.
│   └── Exceptions/DomainExceptions.cs  ← NotFoundException, BusinessRuleException, etc.
│
├── EnterpriseApi.Application/          ← Layer 2: use cases, interfaces, DTOs
│   ├── DTOs/DTOs.cs                    ← request/response shapes (CreateOrderRequest, OrderDto, etc.)
│   ├── Interfaces/Interfaces.cs        ← IUserService, IOrderService, IEventPublisher, IUnitOfWork, etc.
│   ├── Services/Services.cs            ← UserService, ProductService, OrderService, InvoiceService, AuthService
│   ├── Validators/Validators.cs        ← FluentValidation rules for every request DTO
│   └── Extensions/ServiceCollectionExtensions.cs
│
├── EnterpriseApi.Infrastructure/       ← Layer 3: concrete implementations
│   ├── Data/
│   │   ├── AppDbContext.cs             ← EF Core DbContext with audit log interception
│   │   └── Configurations/            ← IEntityTypeConfiguration per entity
│   ├── Repositories/Repositories.cs   ← EF Core repository implementations
│   ├── UnitOfWork.cs                  ← coordinates all repositories in one transaction
│   ├── Messaging/
│   │   ├── KafkaEventPublisher.cs     ← Confluent.Kafka producer
│   │   └── KafkaConsumers.cs          ← IHostedService consumers for each topic
│   ├── BackgroundServices/            ← NotificationProcessor, InvoiceChecker, InventoryMonitor
│   ├── Services/
│   │   ├── InfrastructureServices.cs  ← TokenService, CurrentUserService, DatabaseSeeder
│   ├── Extensions/
│   │   ├── ServiceCollectionExtensions.cs          ← original DI wiring
│   │   └── EnhancedServiceCollectionExtensions.cs  ← billion-user enhancements
│   ├── Caching/CachingStrategy.cs     ← TwoLevelCacheService, CachedProductService
│   ├── Resilience/Resilience.cs       ← Polly pipelines, ResilientEventPublisher
│   ├── Observability/Observability.cs ← EnterpriseApiMetrics, TelemetryMiddleware
│   ├── TwelveFactors/TwelveFactors.cs ← config records, distributed lock, graceful shutdown
│   └── RateLimiting/RateLimiting.cs   ← per-user token bucket policies
│
└── EnterpriseApi.API/                  ← Layer 4: HTTP entry point
    ├── Program.cs                      ← composition root, middleware pipeline
    ├── Controllers/Controllers.cs      ← Auth, Users, Products, Orders, Invoices, Notifications
    ├── Middleware/
    │   └── ExceptionHandlingMiddleware.cs
    ├── appsettings.json
    └── appsettings.Development.json
```

**Dependency rule:** arrows point inward only.
```
API → Infrastructure → Application → Domain
                  ↗
Infrastructure ──
```
Domain knows nothing about anyone. Application knows only Domain. Infrastructure knows Application and Domain. API knows everything.

---

## 3. Architecture Overview

### Clean Architecture layers

**Domain** — the core. Contains entities with business logic baked into methods (`order.Cancel()`, `inventory.Reserve()`). No NuGet packages, no framework references. Completely testable in isolation.

**Application** — orchestrates domain objects. Defines interfaces (`IProductService`, `IEventPublisher`) that Infrastructure must implement. Never imports from Infrastructure.

**Infrastructure** — speaks to the outside world: SQLite/EF Core, Kafka, Redis. Implements every interface defined in Application. Swapping SQLite for PostgreSQL means changing one line here.

**API** — the HTTP shell. Translates HTTP requests into application service calls and back. Contains no business logic.

### Request lifecycle

```
HTTP Request
    │
    ▼
ExceptionHandlingMiddleware   ← catches everything, returns ProblemDetails
    │
    ▼
TelemetryMiddleware           ← injects CorrelationId, records metrics, traces
    │
    ▼
RateLimiter                   ← rejects excess requests before auth
    │
    ▼
UseAuthentication              ← validates JWT, populates HttpContext.User
    │
    ▼
UseAuthorization               ← checks roles/policies
    │
    ▼
Controller                     ← validates request, calls Application service
    │
    ▼
Application Service            ← business rules, calls repository via IUnitOfWork
    │
    ▼
Repository → EF Core → SQLite  ← persistence
    │
    ▼
IEventPublisher → Kafka        ← async event fanout
```

---

## 4. Step-by-Step Local Setup (No Docker)

This mode uses SQLite and a no-op event publisher. No external services required. Ideal for first run and feature development.

### Step 1 — Clone the repository

```bash
git clone <your-repo-url>
cd EnterpriseApi
```

### Step 2 — Verify .NET version

```bash
dotnet --version
# Must print 8.x.x — if not, download .NET 8 SDK from https://dotnet.microsoft.com/download
```

### Step 3 — Restore NuGet packages

```bash
dotnet restore
```

Expected output: `Restore completed in X.Xs`. If you see errors about missing packages, check your internet connection and NuGet source: `dotnet nuget list source`.

### Step 4 — Review appsettings.json

Open `EnterpriseApi.API/appsettings.json`. The defaults work without any changes for local development:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=enterprise.db"
  },
  "JwtSettings": {
    "Secret": "YourSuperSecretKeyThatMustBeAtLeast32CharactersLong!Enterprise2024",
    "Issuer": "EnterpriseApi",
    "Audience": "EnterpriseApiClients",
    "ExpiryMinutes": "60"
  },
  "Kafka": {
    "Enabled": false
  }
}
```

> **⚠️ Security note:** The JWT secret above is for local development only. In any shared or production environment, replace it with a random 64-character string and never commit it to git.

### Step 5 — Build the solution

```bash
dotnet build
```

All four projects should compile with zero errors. Warnings about nullable references are expected.

### Step 6 — Run the API

```bash
cd EnterpriseApi.API
dotnet run
```

You will see output like:

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5267
info: EnterpriseApi.Infrastructure.Services.DatabaseSeeder[0]
      Seeding database...
```

The first run creates `enterprise.db` (SQLite file) in the `EnterpriseApi.API` directory and seeds it with test data. Subsequent runs skip seeding.

### Step 7 — Verify it's working

Open your browser and go to `http://localhost:5267`. Swagger UI loads automatically.

You should see six controller groups: Auth, Users, Products, Orders, Invoices, Notifications.

---

## 5. Step-by-Step Local Setup (With Kafka)

Use this when you want to test event publishing and consumption. Requires Docker Desktop.

### Step 1 — Complete steps 1–5 from the previous section

### Step 2 — Start Kafka with Docker Compose

From the solution root:

```bash
docker-compose up -d zookeeper kafka kafka-ui
```

Wait about 20 seconds for Kafka to finish starting. Check it's healthy:

```bash
docker-compose ps
```

All three services should show `Up`. If Kafka shows `Exit 1`, wait another 10 seconds and run `docker-compose up -d kafka` again (Kafka sometimes starts before Zookeeper is ready).

### Step 3 — Enable Kafka in configuration

In `EnterpriseApi.API/appsettings.json`, change:

```json
"Kafka": {
  "Enabled": true,
  "BootstrapServers": "localhost:9092"
}
```

### Step 4 — Run the application

```bash
cd EnterpriseApi.API
dotnet run
```

You will see Kafka consumer registration in the startup logs:

```
info: EnterpriseApi.Infrastructure.Messaging.KafkaConsumers[0]
      OrderEventConsumer started, consuming from enterprise.orders
```

### Step 5 — View events in Kafka UI

Open `http://localhost:8080`. Click **Topics** in the left sidebar. After placing an order through the API, topics like `enterprise.orders` and `enterprise.inventory` will appear with messages.

### Stopping everything

```bash
# Stop the API: Ctrl+C in the terminal running dotnet run
docker-compose down        # stop containers but keep volumes (data preserved)
docker-compose down -v     # stop containers AND delete all data
```

---

## 6. Running the Application

### Default URLs

| What | URL |
|---|---|
| Swagger UI | `http://localhost:5267` |
| API base | `http://localhost:5267/api/v1` |
| Kafka UI | `http://localhost:8080` (Docker only) |

### Seeded accounts

These are created automatically on first run:

| Email | Password | Role | Can do |
|---|---|---|---|
| `admin@enterprise.com` | `Admin@1234!` | Admin | Everything |
| `manager@enterprise.com` | `Manager@1234!` | Manager | Products, orders, invoices |
| `user@enterprise.com` | `User@1234!` | User | Place orders, view own invoices |

### Getting a JWT token

**Option A — Swagger UI (easiest)**

1. Open `http://localhost:5267`
2. Expand **Auth** → `POST /api/v1/auth/login`
3. Click **Try it out** → paste:
   ```json
   { "email": "admin@enterprise.com", "password": "Admin@1234!" }
   ```
4. Click **Execute** → copy the `token` field from the response
5. Click **Authorize** (top right, padlock icon) → paste `Bearer <your-token>` → click **Authorize**

All subsequent requests in Swagger will include the token automatically.

**Option B — curl**

```bash
curl -X POST http://localhost:5267/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@enterprise.com","password":"Admin@1234!"}' \
  | jq .token
```

Save the token:
```bash
TOKEN="paste-your-token-here"
```

---

## 7. Exploring the API

### Typical workflow — from zero to a paid invoice

Follow these steps in order to exercise the full domain lifecycle:

**1. Login**
```bash
curl -s -X POST http://localhost:5267/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@enterprise.com","password":"Admin@1234!"}' | jq .
```

**2. List products** (seeded products are ready)
```bash
curl -s http://localhost:5267/api/v1/products \
  -H "Authorization: Bearer $TOKEN" | jq .
```

**3. Create an order** (copy a productId from step 2)
```bash
curl -s -X POST http://localhost:5267/api/v1/orders \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "productId": "<paste-product-id>",
    "quantity": 2,
    "notes": "Please ship fast"
  }' | jq .
```

**4. Ship the order** (copy the orderId from step 3)
```bash
curl -s -X PATCH http://localhost:5267/api/v1/orders/<orderId>/ship \
  -H "Authorization: Bearer $TOKEN" | jq .
```

**5. Complete the order**
```bash
curl -s -X PATCH http://localhost:5267/api/v1/orders/<orderId>/complete \
  -H "Authorization: Bearer $TOKEN" | jq .
```

**6. Generate an invoice**
```bash
curl -s -X PATCH http://localhost:5267/api/v1/invoices/<invoiceId>/pay \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"paymentReference":"TXN-2024-001"}' | jq .
```

---

## 8. API Reference

### Auth

| Method | Endpoint | Auth | Description |
|---|---|---|---|
| `POST` | `/api/v1/auth/login` | None | Returns JWT token |
| `POST` | `/api/v1/auth/register` | None | Creates new user account |

### Users

| Method | Endpoint | Auth | Description |
|---|---|---|---|
| `GET` | `/api/v1/users` | Admin, Manager | Paginated user list |
| `GET` | `/api/v1/users/{id}` | Admin, Manager, Self | Get user by ID |
| `POST` | `/api/v1/users` | Admin | Create user |
| `PUT` | `/api/v1/users/{id}` | Admin | Update user |
| `PATCH` | `/api/v1/users/{id}/role` | Admin | Change role |
| `DELETE` | `/api/v1/users/{id}` | Admin | Deactivate user |
| `PATCH` | `/api/v1/users/{id}/activate` | Admin | Reactivate user |

### Products

| Method | Endpoint | Auth | Description |
|---|---|---|---|
| `GET` | `/api/v1/products` | Any authenticated | Paginated product catalog |
| `GET` | `/api/v1/products/{id}` | Any authenticated | Get product by ID |
| `POST` | `/api/v1/products` | Admin | Create product + auto-create inventory |
| `PUT` | `/api/v1/products/{id}` | Admin | Update product |
| `PATCH` | `/api/v1/products/{id}/discount` | Admin, Manager | Apply discount price |
| `PATCH` | `/api/v1/products/{id}/restock` | Admin, Manager | Add inventory units |
| `DELETE` | `/api/v1/products/{id}` | Admin | Deactivate product |

### Orders

| Method | Endpoint | Auth | Description |
|---|---|---|---|
| `GET` | `/api/v1/orders` | Admin, Manager | All orders (paginated) |
| `GET` | `/api/v1/orders/my` | Any authenticated | Current user's orders |
| `GET` | `/api/v1/orders/{id}` | Any authenticated | Get order by ID |
| `POST` | `/api/v1/orders` | Any authenticated | Create order (reserves inventory) |
| `PATCH` | `/api/v1/orders/{id}/cancel` | Any authenticated | Cancel order (releases inventory) |
| `PATCH` | `/api/v1/orders/{id}/ship` | Admin, Manager | Mark as shipped |
| `PATCH` | `/api/v1/orders/{id}/complete` | Admin, Manager | Mark as completed |

### Invoices

| Method | Endpoint | Auth | Description |
|---|---|---|---|
| `GET` | `/api/v1/invoices/my` | Any authenticated | Current user's invoices |
| `GET` | `/api/v1/invoices/{id}` | Any authenticated | Get invoice by ID |
| `GET` | `/api/v1/invoices/order/{orderId}` | Any authenticated | Invoice for an order |
| `GET` | `/api/v1/invoices/overdue` | Admin, Manager | All overdue invoices |
| `PATCH` | `/api/v1/invoices/{id}/pay` | Any authenticated | Mark invoice as paid |

### Notifications

| Method | Endpoint | Auth | Description |
|---|---|---|---|
| `GET` | `/api/v1/notifications/my` | Any authenticated | Current user's notifications |

### Pagination

All list endpoints accept `?page=0&pageSize=20`. Responses follow this shape:

```json
{
  "items": [...],
  "totalCount": 42,
  "page": 0,
  "pageSize": 20,
  "totalPages": 3
}
```

### Error responses

All errors return RFC 7807 Problem Details:

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.4",
  "title": "Not Found",
  "status": 404,
  "detail": "Product with ID 'abc' was not found."
}
```

---

## 9. Configuration Reference

All configuration lives in `EnterpriseApi.API/appsettings.json`. Override any value with an environment variable using double-underscore notation: `Kafka__BootstrapServers=localhost:9092`.

### Connection strings

```json
"ConnectionStrings": {
  "DefaultConnection": "Data Source=enterprise.db"
}
```

For PostgreSQL (production): `"Host=localhost;Database=enterprise;Username=app;Password=secret"`

### JWT settings

```json
"JwtSettings": {
  "Secret": "at-least-32-characters-random-string",
  "Issuer": "EnterpriseApi",
  "Audience": "EnterpriseApiClients",
  "ExpiryMinutes": "60"
}
```

`Secret` must be at least 32 characters. Generate one: `openssl rand -base64 48`

### Kafka

```json
"Kafka": {
  "Enabled": false,
  "BootstrapServers": "localhost:9092",
  "ConsumerGroupId": "enterprise-api",
  "ProducerLingerMs": 5
}
```

Set `Enabled: false` when running without Docker. The `NullEventPublisher` logs events to console but doesn't publish them.

### Redis (billion-user enhancements only)

```json
"Redis": {
  "Enabled": false,
  "ConnectionString": "localhost:6379",
  "DefaultTtlMinutes": 10,
  "KeyPrefix": "enterprise"
}
```

---

## 10. Adding the Billion-User Enhancements

The enhancement files are in `EnterpriseApi.Infrastructure/`:

```
Caching/CachingStrategy.cs      ← Two-level cache (Caffeine L1 + Redis L2)
Resilience/Resilience.cs        ← Polly circuit breaker + retry + outbox
Observability/Observability.cs  ← Prometheus metrics + distributed tracing
TwelveFactors/TwelveFactors.cs  ← Strongly-typed config, distributed lock, graceful shutdown
RateLimiting/RateLimiting.cs    ← Per-user token bucket rate limiter
Extensions/EnhancedServiceCollectionExtensions.cs  ← wires everything above
```

### Step 1 — Install additional NuGet packages

```bash
cd EnterpriseApi.Infrastructure

dotnet add package Microsoft.Extensions.Caching.StackExchangeRedis
dotnet add package StackExchange.Redis
dotnet add package Microsoft.Extensions.Http.Resilience
dotnet add package Polly.Extensions
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Instrumentation.AspNetCore
dotnet add package OpenTelemetry.Instrumentation.EntityFrameworkCore
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
dotnet add package OpenTelemetry.Exporter.Prometheus.AspNetCore
dotnet add package Scrutor

cd ../EnterpriseApi.API
dotnet add package AspNetCore.HealthChecks.Redis
```

### Step 2 — Register enhancements in Program.cs

Add this call after `builder.Services.AddInfrastructure(builder.Configuration)`:

```csharp
// Add billion-user capabilities (caching, resilience, observability, rate limiting)
builder.Services.AddBillionUserCapabilities(builder.Configuration);
```

In the middleware pipeline section, add `UseEnhancedMiddleware()` after building the app and before `UseAuthentication`:

```csharp
var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>(); // keep this first
app.UseEnhancedMiddleware();                      // adds telemetry, rate limiter, Prometheus, health

// then the existing:
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
```

### Step 3 — Add the Database config section to appsettings.json

```json
"Database": {
  "DefaultConnection": "Data Source=enterprise.db",
  "CommandTimeoutSeconds": 30,
  "MaxRetryCount": 3,
  "EnableSensitiveDataLogging": false
}
```

### Step 4 — Start Redis (optional, recommended)

```bash
docker run -d --name enterprise-redis -p 6379:6379 redis:7-alpine
```

Then set `"Redis": { "Enabled": true }` in appsettings.json.

### What you get after enabling enhancements

| Feature | Endpoint / Effect |
|---|---|
| Health check | `GET /health` |
| Ready check (DB + Redis) | `GET /health/ready` |
| Prometheus metrics | `GET /metrics` |
| Rate limiting headers | `X-RateLimit-Remaining`, `Retry-After` on 429 |
| Correlation IDs | `X-Correlation-Id` on every response |
| Slow request warnings | Logged when any request exceeds 500ms |
| Cache hit/miss | Logged at DEBUG level per request |

---

## 11. Project Layer Guide

### How to add a new feature end-to-end

Example: adding a `Review` entity where users can review products.

**Step 1 — Domain layer** (`EnterpriseApi.Domain/Entities/Entities.cs`)

Add the entity class with business logic methods:
```csharp
public class Review
{
    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid UserId { get; private set; }
    public int Rating { get; private set; }
    public string Comment { get; private set; }

    public static Review Create(Guid productId, Guid userId, int rating, string comment)
    {
        if (rating < 1 || rating > 5)
            throw new BusinessRuleException("Rating must be between 1 and 5.");
        return new Review { /* ... */ };
    }
}
```

**Step 2 — Application layer** — add DTOs, interface, and service

In `Interfaces.cs`:
```csharp
public interface IReviewService
{
    Task<ReviewDto> CreateAsync(Guid productId, CreateReviewRequest request, CancellationToken ct = default);
    Task<PagedResult<ReviewDto>> GetByProductAsync(Guid productId, int page, int pageSize, CancellationToken ct = default);
}
```

**Step 3 — Infrastructure layer** — add EF Core config and repository

In `AppDbContext.cs`: `public DbSet<Review> Reviews { get; set; }`

Add a `ReviewConfiguration : IEntityTypeConfiguration<Review>` in `Configurations/`.

Add `IReviewRepository` to `IUnitOfWork` and implement it in `Repositories.cs`.

Implement `ReviewService : IReviewService` in `Services.cs`.

Register in `ServiceCollectionExtensions.cs`: `services.AddScoped<IReviewService, ReviewService>();`

**Step 4 — API layer** — add controller

```csharp
[Route("api/v1/products/{productId:guid}/reviews")]
public class ReviewsController : ControllerBase
{
    [HttpGet] public async Task<IActionResult> GetAll(...) { ... }
    [HttpPost] [Authorize] public async Task<IActionResult> Create(...) { ... }
}
```

### How to add a new background service

1. Create a class implementing `BackgroundService` in `BackgroundServices/`
2. Override `ExecuteAsync(CancellationToken stoppingToken)`
3. Register in `ServiceCollectionExtensions.cs`: `services.AddHostedService<YourService>();`

### How to add a new Kafka topic

1. Add topic name constant in `KafkaEventPublisher.cs`
2. Create a consumer class implementing `BackgroundService` in `KafkaConsumers.cs`
3. Register the consumer in `AddBackgroundServices()`

---

## 12. Common Issues and Fixes

### `enterprise.db` lock error on startup

```
SQLite Error 5: 'database is locked'
```

Another process has the SQLite file open (a previous crashed instance). Kill all dotnet processes:

```bash
# Windows
taskkill /f /im dotnet.exe

# macOS/Linux
pkill -f "dotnet run"
```

### Port 5267 already in use

```
System.IO.IOException: Failed to bind to address http://localhost:5267: address already in use
```

Either kill the process using that port or change the port in `launchSettings.json` or by setting an environment variable:

```bash
ASPNETCORE_URLS=http://localhost:5300 dotnet run
```

### JWT token rejected (401) even though it looks valid

Common causes:
- Token expired (default 60-minute lifetime) — log in again
- Clock skew between your machine and the API is > 1 minute — sync your system clock
- Pasting the token without the `Bearer ` prefix in Swagger — the Authorize dialog needs `Bearer <token>`, not just the token

### Kafka connection refused on startup

```
Confluent.Kafka.KafkaException: Local: Broker transport failure
```

Either Docker is not running, or you forgot to set `"Kafka": { "Enabled": false }`. Set it to false to run without Kafka.

### `No service for type 'Program'` error

You referenced `ILogger<Program>` in an Infrastructure class. Use `ILogger<YourClassName>` instead — `Program` only exists in the API project and creates a circular dependency.

### Migrations not found

If you switch from SQLite to PostgreSQL and run `dotnet ef database update`, ensure you run the command from the `EnterpriseApi.API` project directory:

```bash
cd EnterpriseApi.API
dotnet ef database update --project ../EnterpriseApi.Infrastructure
```

---

## Background services

Three background services run automatically when the application starts:

| Service | What it does | Interval |
|---|---|---|
| `NotificationProcessorService` | Picks up pending notifications and "sends" them | Every 15 seconds |
| `OverdueInvoiceCheckerService` | Marks unpaid invoices past due date as overdue | Every 60 minutes |
| `InventoryMonitorService` | Publishes low-stock events when inventory falls below threshold | Every 30 minutes |

In development you'll see their log lines in the console. They run on background threads and don't block HTTP requests.

---

## Domain state machines

**Order lifecycle:**
```
Pending → Shipped → Completed
   │
   └→ Cancelled  (only from Pending or Shipped)
```

**Invoice lifecycle:**
```
Pending → Paid
   │
   └→ Overdue  (set by background service when past due date)
```

Attempting an invalid transition (e.g. completing a cancelled order) returns `422 Unprocessable Entity` with a clear error message.
