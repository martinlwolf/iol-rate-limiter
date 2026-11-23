using System.Collections.Concurrent;

namespace RateLimiter.Core;

// Token bucket (pp. 54–58): one bucket per client holding up to `capacity` tokens, fully refilled over
// `refillPeriod`. Each request takes one token; bursts are allowed while tokens remain.
//
// Unlike Fig. 4-6, which adds tokens in one batch per period, refill here is continuous and computed on each
// request from the elapsed time, so no per-bucket timer is needed.
public sealed partial class TokenBucketRateLimiter : IRateLimiter
{
    private readonly int _capacity;
    private readonly TimeSpan _refillPeriod;
    private readonly TimeProvider _time;

    // The dictionary is thread-safe, but each bucket's contents are not: TryAcquire guards them with a lock.
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public TokenBucketRateLimiter(int capacity, TimeSpan refillPeriod, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (refillPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refillPeriod), "Refill period must be greater than zero.");
        }
        ArgumentNullException.ThrowIfNull(time);

        _capacity = capacity;
        _refillPeriod = refillPeriod;
        _time = time;
    }

    public RateLimitDecision TryAcquire(string clientKey)
    {
        // Only loops again if RemoveIdle removed the bucket between GetOrAdd and taking the lock.
        while (true)
        {
            Bucket bucket = _buckets.GetOrAdd(clientKey, _ => new Bucket(_capacity, _time.GetUtcNow()));

            // Refill, check and take is a read-modify-write: without mutual exclusion, two concurrent requests
            // from the same client read the same balance and one extra gets through (lost update, Fig. 4-14).
            // The lock is per bucket so different clients never contend.
            lock (bucket)
            {
                if (bucket.Removed)
                {
                    // Using it would record the request on an orphaned bucket, and the next request would
                    // create a full one.
                    continue;
                }

                // Read the clock inside the lock so each bucket sees time move forward in order.
                Refill(bucket, _time.GetUtcNow());

                if (bucket.Tokens >= 1)
                {
                    bucket.Tokens -= 1;
                    int remaining = (int)Math.Floor(bucket.Tokens);
                    return new RateLimitDecision(Allowed: true, _capacity, remaining, RetryAfter: TimeSpan.Zero);
                }

                // Multiply before dividing so round cases come out exact (1 × 60 / 4 = 15 s).
                double secondsUntilNextToken = (1 - bucket.Tokens) * _refillPeriod.TotalSeconds / _capacity;
                // Rounded up to the tick so we never report a shorter wait than needed.
                var retryAfter = TimeSpan.FromTicks((long)Math.Ceiling(secondsUntilNextToken * TimeSpan.TicksPerSecond));
                return new RateLimitDecision(Allowed: false, _capacity, Remaining: 0, retryAfter);
            }
        }
    }

    // Removes buckets unused for a full refill period: they are already full, same as a new one, so the
    // client neither gains nor loses anything. That is why the threshold comes from the algorithm, not config.
    //
    // Removal happens under the bucket's lock and sets Removed; a TryAcquire waiting on that lock sees it and
    // looks the bucket up again. No extra lock is needed.
    public int RemoveIdle()
    {
        int removed = 0;

        foreach (KeyValuePair<string, Bucket> entry in _buckets)
        {
            lock (entry.Value)
            {
                bool idleForFullRefill = _time.GetUtcNow() - entry.Value.LastRefill >= _refillPeriod;
                if (entry.Value.Removed || !idleForFullRefill)
                {
                    continue;
                }

                entry.Value.Removed = true;
                // This overload only removes the entry if the key still maps to this same bucket.
                if (_buckets.TryRemove(entry))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    // Requires the bucket's lock. Tokens above capacity are discarded (p. 55).
    private void Refill(Bucket bucket, DateTimeOffset now)
    {
        // If the system clock goes backwards, keep the last refill time: moving it back would refill the
        // bucket a second time once the clock catches up.
        if (now <= bucket.LastRefill)
        {
            return;
        }

        double elapsedSeconds = (now - bucket.LastRefill).TotalSeconds;
        double refilled = elapsedSeconds * _capacity / _refillPeriod.TotalSeconds;
        bucket.Tokens = Math.Min(_capacity, bucket.Tokens + refilled);
        bucket.LastRefill = now;
    }
}
