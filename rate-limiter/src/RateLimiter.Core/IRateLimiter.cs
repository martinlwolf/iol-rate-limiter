namespace RateLimiter.Core;

// Common contract for the algorithms. Each implementation encapsulates its own state, synchronization and
// math; the middleware does not know which one it is using.
public interface IRateLimiter
{
    // Decides whether the request is allowed and, if so, counts it. clientKey is opaque to the algorithm
    // (currently the IP address).
    RateLimitDecision TryAcquire(string clientKey);

    // Removes clients whose state is already identical to a new client's, so removing them changes no
    // decision. Each algorithm defines when that is. Called from another thread while requests keep arriving.
    int RemoveIdle();
}
