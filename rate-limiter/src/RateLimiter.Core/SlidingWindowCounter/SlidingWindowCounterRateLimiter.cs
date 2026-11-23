using System.Collections.Concurrent;

namespace RateLimiter.Core;

// Sliding window counter (pp. 61–62). Counts requests in the current and previous fixed windows and
// estimates the rolling window as:
//
//     current + previous × (1 − elapsed fraction of the current window)
//
// Avoids the fixed window's edge burst (Fig. 4-9) with two counters per client instead of one timestamp per
// request like the sliding window log. Assumes the previous window was evenly distributed; Cloudflare
// measured a 0.003% error rate.
public sealed partial class SlidingWindowCounterRateLimiter : IRateLimiter
{
    private readonly int _limit;
    private readonly long _windowTicks;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Counters> _counters = new();

    public SlidingWindowCounterRateLimiter(int limit, TimeSpan window, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be greater than zero.");
        }
        ArgumentNullException.ThrowIfNull(time);

        _limit = limit;
        _windowTicks = window.Ticks;
        _time = time;
    }

    // Same concurrency structure as TokenBucketRateLimiter.TryAcquire.
    public RateLimitDecision TryAcquire(string clientKey)
    {
        while (true)
        {
            Counters counters = _counters.GetOrAdd(clientKey, _ => new Counters());

            // Estimating and incrementing must be atomic; otherwise there is a lost update (Fig. 4-14).
            lock (counters)
            {
                if (counters.Removed)
                {
                    continue;
                }

                DateTimeOffset now = _time.GetUtcNow();
                MoveToCurrentWindow(counters, now);

                double previousWeight = 1 - ElapsedFractionOfWindow(now);
                double estimated = counters.Current + counters.Previous * previousWeight;

                // Rounded down, as in the book's example (6.5 → 6).
                if (Math.Floor(estimated) < _limit)
                {
                    counters.Current++;
                    int remaining = Math.Max(0, _limit - (int)Math.Floor(estimated) - 1);
                    return new RateLimitDecision(Allowed: true, _limit, remaining, RetryAfter: TimeSpan.Zero);
                }

                // Rejected requests are not counted.
                return new RateLimitDecision(Allowed: false, _limit, Remaining: 0, TimeUntilAllowed(counters, now));
            }
        }
    }

    // After two windows without requests both counters are 0: the state matches a new client's.
    // Same removal strategy as TokenBucketRateLimiter.
    public int RemoveIdle()
    {
        long currentWindow = _time.GetUtcNow().UtcTicks / _windowTicks;
        int removed = 0;

        foreach (KeyValuePair<string, Counters> entry in _counters)
        {
            lock (entry.Value)
            {
                bool idleForTwoWindows = currentWindow - entry.Value.WindowIndex >= 2;
                if (entry.Value.Removed || !idleForTwoWindows)
                {
                    continue;
                }

                entry.Value.Removed = true;
                if (_counters.TryRemove(entry))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    // Windows are aligned to the clock (UtcTicks / size), so a 1-minute window is a whole minute.
    private void MoveToCurrentWindow(Counters counters, DateTimeOffset now)
    {
        long windowIndex = now.UtcTicks / _windowTicks;

        // If the clock goes backwards nothing is reset: resetting would hand the client free requests.
        if (windowIndex <= counters.WindowIndex)
        {
            return;
        }

        counters.Previous = windowIndex == counters.WindowIndex + 1 ? counters.Current : 0;
        counters.Current = 0;
        counters.WindowIndex = windowIndex;
    }

    private double ElapsedFractionOfWindow(DateTimeOffset now) =>
        (double)(now.UtcTicks % _windowTicks) / _windowTicks;

    // Solves the estimate formula for the time when a request will be allowed again:
    // - Current < limit: waiting for the previous window's weight to drop is enough,
    //   f > 1 − (limit − Current) / Previous. If rejected with Current < limit, Previous > 0.
    // - Current ≥ limit: wait for the next window, where Current becomes Previous.
    // One tick is added because the check is strict: at the exact instant the estimate equals the limit.
    private TimeSpan TimeUntilAllowed(Counters counters, DateTimeOffset now)
    {
        long elapsedTicks = now.UtcTicks % _windowTicks;
        long waitTicks;

        if (counters.Current < _limit)
        {
            double targetFraction = 1 - (double)(_limit - counters.Current) / counters.Previous;
            waitTicks = (long)Math.Ceiling(targetFraction * _windowTicks) - elapsedTicks;
        }
        else
        {
            double targetFractionOfNextWindow = Math.Max(0, 1 - (double)_limit / counters.Current);
            waitTicks = _windowTicks - elapsedTicks + (long)Math.Ceiling(targetFractionOfNextWindow * _windowTicks);
        }

        return TimeSpan.FromTicks(Math.Max(0, waitTicks) + 1);
    }
}
