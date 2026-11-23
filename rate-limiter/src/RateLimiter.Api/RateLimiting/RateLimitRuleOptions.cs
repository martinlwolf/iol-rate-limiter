namespace RateLimiter.Api.RateLimiting;

// One rule from appsettings.json, for example:
//   "RateLimiting": { "Rules": { "login": { "Algorithm": "TokenBucket", "Limit": 5, "Window": "00:01:00" } } }
public sealed class RateLimitRuleOptions
{
    // "TokenBucket" or "SlidingWindowCounter". A string rather than an enum so a typo gets a clear error.
    public string Algorithm { get; set; } = "";

    // Maximum requests per window. For TokenBucket, the bucket capacity.
    public int Limit { get; set; }

    // For TokenBucket, the time it takes to refill an empty bucket.
    public TimeSpan Window { get; set; }
}
