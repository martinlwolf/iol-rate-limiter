namespace RateLimiter.Tests;

// FakeTimeProvider does not allow moving time backwards, so clock-adjustment tests use this one.
internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
