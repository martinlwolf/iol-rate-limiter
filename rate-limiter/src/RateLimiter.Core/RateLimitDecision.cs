namespace RateLimiter.Core;

// Result of TryAcquire. Carries Limit, Remaining and RetryAfter (not just a bool) because the middleware needs
// them for the X-Ratelimit-* headers (pp. 64–65), and each algorithm computes them differently.
// Remaining is what is left after this request; RetryAfter is zero when the request is allowed.
public sealed record RateLimitDecision(bool Allowed, int Limit, int Remaining, TimeSpan RetryAfter);
