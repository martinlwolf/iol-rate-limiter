namespace RateLimiter.Core;

// partial so the state lives in its own file while staying private to the algorithm.
public sealed partial class TokenBucketRateLimiter
{
    // Per-client state. Only read or modified while holding its lock.
    private sealed class Bucket(double tokens, DateTimeOffset lastRefill)
    {
        // double because refill is continuous: a bucket can hold 2.5 tokens.
        public double Tokens = tokens;

        // Updated on every request, so it also marks the last use (RemoveIdle relies on this).
        public DateTimeOffset LastRefill = lastRefill;

        // Set by RemoveIdle when it takes the bucket out of the dictionary (see TryAcquire).
        public bool Removed;
    }
}
