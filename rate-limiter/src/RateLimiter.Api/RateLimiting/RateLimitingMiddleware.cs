using System.Diagnostics.Metrics;
using RateLimiter.Core;

namespace RateLimiter.Api.RateLimiting;

// Applies the endpoint's [RateLimit] rule: lets the request through or responds with 429.
// Must run after UseRouting, which resolves the endpoint and its metadata.
public sealed class RateLimitingMiddleware(
    RequestDelegate next,
    RateLimitRules rules,
    ILogger<RateLimitingMiddleware> logger)
{
    // Allowed and rejected requests per rule, to spot badly tuned rules (p. 68).
    // View with: dotnet-counters monitor -n RateLimiter.Api --counters RateLimiter.Api
    private static readonly Meter Meter = new("RateLimiter.Api");
    private static readonly Counter<long> AllowedRequests =
        Meter.CreateCounter<long>("ratelimiter.allowed", description: "Requests allowed by the rate limiter");
    private static readonly Counter<long> RejectedRequests =
        Meter.CreateCounter<long>("ratelimiter.rejected", description: "Requests rejected with 429");

    public async Task InvokeAsync(HttpContext context)
    {
        RateLimitAttribute? rateLimit = context.GetEndpoint()?.Metadata.GetMetadata<RateLimitAttribute>();
        if (rateLimit is null)
        {
            await next(context);
            return;
        }

        // Without authentication, the IP is the only trustworthy key. It can be null (e.g. under TestServer).
        string clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        RateLimitDecision decision;
        try
        {
            decision = rules.Get(rateLimit.RuleName).TryAcquire(clientKey);
        }
        catch (Exception ex)
        {
            // Fail open: a limiter failure must not take the API down (p. 52), even though it stops protecting
            // in the meantime. The try covers only the limiter, not controller exceptions.
            logger.LogError(ex,
                "Rate limiter failed (rule {Rule}, client {ClientKey}). Failing open: request allowed without limit.",
                rateLimit.RuleName, clientKey);
            await next(context);
            return;
        }

        // Sent on allowed responses too, so clients know how much they have left (pp. 64–65).
        context.Response.Headers["X-Ratelimit-Limit"] = decision.Limit.ToString();
        context.Response.Headers["X-Ratelimit-Remaining"] = decision.Remaining.ToString();

        var ruleTag = new KeyValuePair<string, object?>("rule", rateLimit.RuleName);

        if (decision.Allowed)
        {
            AllowedRequests.Add(1, ruleTag);
            await next(context);
            return;
        }

        RejectedRequests.Add(1, ruleTag);

        // Rounded up: 0.3 s left is reported as 1. With 0 the client would retry and get another 429.
        int retryAfterSeconds = (int)Math.Ceiling(decision.RetryAfter.TotalSeconds);
        context.Response.Headers["X-Ratelimit-Retry-After"] = retryAfterSeconds.ToString();
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        // Only rejections are logged: allowed requests would be noise and would flood the log during an attack.
        logger.LogInformation(
            "Rate limit exceeded: rule {Rule}, client {ClientKey}, retry after {RetryAfterSeconds} s.",
            rateLimit.RuleName, clientKey, retryAfterSeconds);

        await context.Response.WriteAsJsonAsync(new
        {
            error = "Too many requests",
            rule = rateLimit.RuleName,
            retryAfterSeconds,
        });
    }
}
