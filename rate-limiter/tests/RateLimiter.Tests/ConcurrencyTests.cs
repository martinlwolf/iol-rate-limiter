using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core;

namespace RateLimiter.Tests;

// Checks for lost updates (Fig. 4-14) with many threads hitting the same client. The clock is frozen, so
// there is no refill or window change: exactly Limit requests must pass. The race is intermittent, hence the
// repetitions. Without the lock in TryAcquire this test fails.
public sealed class ConcurrencyTests
{
    private const int Limit = 100;
    private const int Requests = 1_000;
    private const int Repetitions = 20;

    [Theory]
    [InlineData("TokenBucket")]
    [InlineData("SlidingWindowCounter")]
    public void Con_muchos_hilos_contra_el_mismo_cliente_pasan_exactamente_limit(string algorithm)
    {
        for (int repetition = 0; repetition < Repetitions; repetition++)
        {
            var time = new FakeTimeProvider();
            IRateLimiter limiter = algorithm == "TokenBucket"
                ? new TokenBucketRateLimiter(Limit, TimeSpan.FromMinutes(1), time)
                : new SlidingWindowCounterRateLimiter(Limit, TimeSpan.FromMinutes(1), time);

            int allowed = 0;
            Parallel.For(0, Requests, _ =>
            {
                if (limiter.TryAcquire("same-client").Allowed)
                {
                    Interlocked.Increment(ref allowed);
                }
            });

            Assert.Equal(Limit, allowed);
        }
    }

    [Fact]
    public async Task La_limpieza_concurrente_no_regala_requests()
    {
        // With the clock frozen no bucket is idle: if cleanup removed one in use, the client would get a
        // full bucket and more than Limit requests would pass.
        var time = new FakeTimeProvider();
        var limiter = new TokenBucketRateLimiter(Limit, TimeSpan.FromMinutes(1), time);

        int allowed = 0;
        using var cleanupRunning = new CancellationTokenSource();
        Task cleanup = Task.Run(() =>
        {
            while (!cleanupRunning.IsCancellationRequested)
            {
                limiter.RemoveIdle();
            }
        });

        Parallel.For(0, Requests, _ =>
        {
            if (limiter.TryAcquire("same-client").Allowed)
            {
                Interlocked.Increment(ref allowed);
            }
        });

        cleanupRunning.Cancel();
        await cleanup;
        Assert.Equal(Limit, allowed);
    }
}
