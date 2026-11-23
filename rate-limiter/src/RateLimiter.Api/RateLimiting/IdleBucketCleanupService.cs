namespace RateLimiter.Api.RateLimiting;

// Periodically removes idle clients. An in-memory dictionary does not expire entries like Redis EXPIRE
// (p. 62); without this, a client rotating IPs would grow memory without bound.
// No lock of its own: each algorithm removes under the bucket's lock (see TokenBucketRateLimiter.RemoveIdle).
public sealed class IdleBucketCleanupService : BackgroundService
{
    private readonly RateLimitRules _rules;
    private readonly TimeProvider _time;
    private readonly ILogger<IdleBucketCleanupService> _logger;
    private readonly TimeSpan _interval;

    public IdleBucketCleanupService(
        RateLimitRules rules,
        IConfiguration configuration,
        TimeProvider time,
        ILogger<IdleBucketCleanupService> logger)
    {
        _rules = rules;
        _time = time;
        _logger = logger;

        // Validated in the constructor, which runs before the server accepts requests. In ExecuteAsync the app
        // would already be running and an invalid interval would crash it with an unclear error.
        _interval = configuration.GetValue("RateLimiting:CleanupInterval", TimeSpan.FromMinutes(1));
        if (_interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"RateLimiting:CleanupInterval must be greater than zero (current value: {_interval}).");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Using the injected TimeProvider lets the tests' fake clock drive the timer too.
        using var timer = new PeriodicTimer(_interval, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    int removed = _rules.RemoveIdleBuckets();
                    _logger.LogDebug("Cleanup: removed {Removed} idle clients.", removed);
                }
                catch (Exception ex)
                {
                    // An unhandled exception would stop the BackgroundService and, by default, the app.
                    _logger.LogError(ex, "Idle client cleanup failed. Retrying on the next cycle.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
