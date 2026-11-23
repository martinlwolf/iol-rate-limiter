namespace RateLimiter.Core;

public sealed partial class SlidingWindowCounterRateLimiter
{
    // Per-client state. Only read or modified while holding its lock.
    private sealed class Counters
    {
        public long WindowIndex; // window that Current belongs to
        public int Current;      // allowed requests in window WindowIndex
        public int Previous;     // allowed requests in window WindowIndex - 1
        public bool Removed;     // set by RemoveIdle (see TryAcquire)
    }
}
