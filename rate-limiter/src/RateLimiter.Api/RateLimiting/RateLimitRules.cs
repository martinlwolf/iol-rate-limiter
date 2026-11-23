using RateLimiter.Core;

namespace RateLimiter.Api.RateLimiting;

// The configured rules, with one algorithm (and its own counters) per rule. Validates the config when
// constructed, so an invalid rule prevents the app from starting.
public sealed class RateLimitRules
{
    // Filled in the constructor and only read afterwards, so it is shared across threads without a lock.
    // Case-insensitive, like .NET configuration keys.
    private readonly Dictionary<string, IRateLimiter> _limiters = new(StringComparer.OrdinalIgnoreCase);

    public RateLimitRules(IReadOnlyDictionary<string, RateLimitRuleOptions> rules, TimeProvider time)
    {
        foreach (var (name, rule) in rules)
        {
            _limiters[name] = CreateLimiter(name, rule, time);
        }
    }

    // An unknown rule should never get here: EnsureRulesExist checks them at startup.
    public IRateLimiter Get(string ruleName) => _limiters[ruleName];

    public int RemoveIdleBuckets() => _limiters.Values.Sum(limiter => limiter.RemoveIdle());

    // A typo such as [RateLimit("logn")] stops startup instead of leaving the endpoint unprotected.
    public void EnsureRulesExist(IEnumerable<string> ruleNames)
    {
        List<string> missing = ruleNames.Where(name => !_limiters.ContainsKey(name)).Distinct().ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Some endpoints use [RateLimit] rules that are not defined in RateLimiting:Rules: {string.Join(", ", missing)}. " +
                $"Configured rules: {string.Join(", ", _limiters.Keys)}.");
        }
    }

    private static IRateLimiter CreateLimiter(string name, RateLimitRuleOptions rule, TimeProvider time)
    {
        // Validated here so the message names the rule; the algorithm constructors' messages do not.
        if (rule.Limit <= 0 || rule.Window <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Rule '{name}' is invalid: Limit and Window must be greater than zero (Limit={rule.Limit}, Window={rule.Window}).");
        }

        switch (rule.Algorithm)
        {
            case "TokenBucket":
                return new TokenBucketRateLimiter(rule.Limit, rule.Window, time);
            case "SlidingWindowCounter":
                return new SlidingWindowCounterRateLimiter(rule.Limit, rule.Window, time);
            default:
                throw new InvalidOperationException(
                    $"Rule '{name}' has an unknown algorithm: '{rule.Algorithm}'. Valid values: TokenBucket, SlidingWindowCounter.");
        }
    }
}
