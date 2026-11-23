namespace RateLimiter.Api.RateLimiting;

// Names the RateLimiting:Rules rule an endpoint uses. On a controller it applies to all its endpoints; on a
// method, only to that one (and takes precedence). Endpoints without it are not limited.
//
// The middleware reads it from the metadata of the endpoint the router already resolved, so it never compares
// URLs (/api/orders/42, prefixes, casing). Limits live in config; the endpoint only says which one applies.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RateLimitAttribute(string ruleName) : Attribute
{
    public string RuleName { get; } = ruleName;
}
