using Microsoft.AspNetCore.Mvc;

namespace RateLimiter.Api.Controllers;

// No [RateLimit]: never returns 429 and sends no X-Ratelimit-* headers.
[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok" });
}
