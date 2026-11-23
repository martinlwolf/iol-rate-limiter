using Microsoft.Extensions.Time.Testing;
using RateLimiter.Api.RateLimiting;
using RateLimiter.Core;

namespace RateLimiter.Tests;

public sealed class RateLimitRulesTests
{
    private readonly FakeTimeProvider _time = new();

    private static RateLimitRuleOptions Rule(string algorithm, int limit = 5, int windowSeconds = 60) =>
        new() { Algorithm = algorithm, Limit = limit, Window = TimeSpan.FromSeconds(windowSeconds) };

    [Fact]
    public void Crea_un_algoritmo_por_regla_segun_la_config()
    {
        var rules = new RateLimitRules(new Dictionary<string, RateLimitRuleOptions>
        {
            ["login"] = Rule("TokenBucket"),
            ["orders"] = Rule("SlidingWindowCounter"),
        }, _time);

        Assert.IsType<TokenBucketRateLimiter>(rules.Get("login"));
        Assert.IsType<SlidingWindowCounterRateLimiter>(rules.Get("orders"));
        Assert.IsType<TokenBucketRateLimiter>(rules.Get("LOGIN"));
    }

    [Fact]
    public void Cada_regla_tiene_sus_propios_contadores()
    {
        var rules = new RateLimitRules(new Dictionary<string, RateLimitRuleOptions>
        {
            ["login"] = Rule("TokenBucket", limit: 1),
            ["orders"] = Rule("TokenBucket", limit: 1),
        }, _time);

        Assert.True(rules.Get("login").TryAcquire("ip-1").Allowed);
        Assert.False(rules.Get("login").TryAcquire("ip-1").Allowed);
        Assert.True(rules.Get("orders").TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void Un_algoritmo_desconocido_falla_con_un_mensaje_claro()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new RateLimitRules(
            new Dictionary<string, RateLimitRuleOptions> { ["login"] = Rule("LeakyBucket") }, _time));

        Assert.Contains("login", error.Message);
        Assert.Contains("LeakyBucket", error.Message);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(5, 0)] // what you get if Window is missing from config
    [InlineData(-1, 60)]
    public void Limit_o_Window_invalidos_fallan_con_un_mensaje_claro(int limit, int windowSeconds)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new RateLimitRules(
            new Dictionary<string, RateLimitRuleOptions> { ["login"] = Rule("TokenBucket", limit, windowSeconds) }, _time));

        Assert.Contains("login", error.Message);
    }

    [Fact]
    public void Un_typo_en_RateLimit_falla_al_arrancar_y_dice_que_regla_falta()
    {
        var rules = new RateLimitRules(
            new Dictionary<string, RateLimitRuleOptions> { ["login"] = Rule("TokenBucket") }, _time);

        var error = Assert.Throws<InvalidOperationException>(() => rules.EnsureRulesExist(["login", "logn"]));

        Assert.Contains("logn", error.Message);
    }

    [Fact]
    public void Si_todas_las_reglas_existen_no_falla()
    {
        var rules = new RateLimitRules(
            new Dictionary<string, RateLimitRuleOptions> { ["login"] = Rule("TokenBucket") }, _time);

        rules.EnsureRulesExist(["login", "Login"]);
    }

    [Fact]
    public void RemoveIdleBuckets_limpia_todas_las_reglas()
    {
        var rules = new RateLimitRules(new Dictionary<string, RateLimitRuleOptions>
        {
            ["login"] = Rule("TokenBucket"),
            ["orders"] = Rule("SlidingWindowCounter"),
        }, _time);
        rules.Get("login").TryAcquire("ip-1");
        rules.Get("orders").TryAcquire("ip-1");

        _time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(2, rules.RemoveIdleBuckets());
    }
}
