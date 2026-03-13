using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EnterpriseApi.API.Controllers;

// ─────────────────────────────────────────────────────────────────────────────
// HEALTH CHECK CONTROLLER
//
// Required by the ECS target group and load balancer.
// ECS hits GET /health every 30 seconds. If it returns non-200 three times in
// a row, ECS marks the task as unhealthy and replaces it automatically.
//
// Two endpoints:
//   GET /health         — simple liveness check (is the process alive?)
//   GET /health/ready   — readiness check (is the app ready to serve traffic?)
//   GET /health/version — what version is deployed? (used by CD smoke tests)
// ─────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    private readonly HealthCheckService _healthCheckService;
    private readonly IConfiguration _configuration;

    public HealthController(HealthCheckService healthCheckService,
        IConfiguration configuration)
    {
        _healthCheckService = healthCheckService;
        _configuration = configuration;
    }

    /// <summary>
    /// Liveness probe — is the process running?
    /// ECS and the ALB use this. Always returns 200 if the process is alive.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(200)]
    public IActionResult Live()
    {
        return Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Readiness probe — is the app ready to handle traffic?
    /// Checks downstream dependencies (database, etc.).
    /// Returns 503 if any critical dependency is down.
    /// </summary>
    [HttpGet("ready")]
    [ProducesResponseType(200)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> Ready(CancellationToken ct)
    {
        var result = await _healthCheckService.CheckHealthAsync(ct);

        var response = new
        {
            status = result.Status.ToString(),
            timestamp = DateTime.UtcNow,
            checks = result.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = e.Value.Duration.TotalMilliseconds
            })
        };

        if (result.Status == HealthStatus.Healthy)
            return Ok(response);

        return StatusCode(503, response);
    }

    /// <summary>
    /// Version endpoint — which build is running?
    /// Used by the CD pipeline smoke test to confirm the correct version deployed.
    /// </summary>
    [HttpGet("version")]
    [ProducesResponseType(200)]
    public IActionResult Version()
    {
        return Ok(new
        {
            version = Environment.GetEnvironmentVariable("BUILD_VERSION") ?? "local",
            commit = Environment.GetEnvironmentVariable("GIT_COMMIT") ?? "unknown",
            environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            timestamp = DateTime.UtcNow
        });
    }
}
