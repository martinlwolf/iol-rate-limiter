using Microsoft.AspNetCore.Mvc;
using RateLimiter.Api.RateLimiting;

namespace RateLimiter.Api.Controllers;

// Demo endpoints. [RateLimit] at controller level: /api/orders and /api/orders/{id} share the limit.
[ApiController]
[Route("api/orders")]
[RateLimit("orders")]
public sealed class OrdersController : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok(new[] { new { id = 1 }, new { id = 2 } });

    [HttpGet("{id:int}")]
    public IActionResult GetById(int id) => Ok(new { id });
}
