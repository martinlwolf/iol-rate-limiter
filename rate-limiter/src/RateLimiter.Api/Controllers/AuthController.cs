using Microsoft.AspNetCore.Mvc;
using RateLimiter.Api.RateLimiting;

namespace RateLimiter.Api.Controllers;

// Demo endpoint, no real login. [RateLimit] applied at method level.
[ApiController]
[Route("api")]
public sealed class AuthController : ControllerBase
{
    [HttpPost("login")]
    [RateLimit("login")]
    public IActionResult Login() => Ok(new { message = "login ok" });
}
