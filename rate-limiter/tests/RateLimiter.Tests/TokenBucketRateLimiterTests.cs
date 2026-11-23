using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core;

namespace RateLimiter.Tests;

// Defaults to 4 tokens refilled over 1 minute (1 every 15 s), as in Fig. 4-6.
public sealed class TokenBucketRateLimiterTests
{
    private readonly FakeTimeProvider _time = new();

    private TokenBucketRateLimiter CreateLimiter(int capacity = 4, int refillSeconds = 60) =>
        new(capacity, TimeSpan.FromSeconds(refillSeconds), _time);

    [Fact]
    public void Rafaga_hasta_la_capacidad_pasa_y_el_siguiente_se_rechaza()
    {
        var limiter = CreateLimiter();

        for (int expectedRemaining = 3; expectedRemaining >= 0; expectedRemaining--)
        {
            RateLimitDecision decision = limiter.TryAcquire("ip-1");
            Assert.True(decision.Allowed);
            Assert.Equal(4, decision.Limit);
            Assert.Equal(expectedRemaining, decision.Remaining);
        }

        RateLimitDecision rejected = limiter.TryAcquire("ip-1");
        Assert.False(rejected.Allowed);
        Assert.Equal(0, rejected.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(15), rejected.RetryAfter);
    }

    [Fact]
    public void Se_recarga_un_token_cada_15_segundos()
    {
        var limiter = CreateLimiter();
        ConsumeAll(limiter, "ip-1", 4);

        _time.Advance(TimeSpan.FromSeconds(14));
        Assert.False(limiter.TryAcquire("ip-1").Allowed);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryAcquire("ip-1").Allowed);
        Assert.False(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void Los_tokens_de_mas_se_desbordan_y_no_superan_la_capacidad()
    {
        var limiter = CreateLimiter();
        ConsumeAll(limiter, "ip-1", 4);

        // Enough time for 40 tokens, but the bucket is capped at its capacity (p. 55).
        _time.Advance(TimeSpan.FromMinutes(10));

        ConsumeAll(limiter, "ip-1", 4);
        Assert.False(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void Cada_cliente_tiene_su_propio_balde()
    {
        var limiter = CreateLimiter();
        ConsumeAll(limiter, "ip-1", 4);

        Assert.False(limiter.TryAcquire("ip-1").Allowed);
        Assert.True(limiter.TryAcquire("ip-2").Allowed);
    }

    [Fact]
    public void Despues_de_esperar_el_RetryAfter_el_request_pasa()
    {
        // 1 token every 3.33 s: RetryAfter is not a round number and must still be enough.
        var limiter = CreateLimiter(capacity: 3, refillSeconds: 10);
        ConsumeAll(limiter, "ip-1", 3);
        _time.Advance(TimeSpan.FromSeconds(1)); // 0.3 tokens

        RateLimitDecision rejected = limiter.TryAcquire("ip-1");
        Assert.False(rejected.Allowed);

        _time.Advance(rejected.RetryAfter);
        Assert.True(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void RemoveIdle_borra_solo_los_baldes_que_ya_se_recargaron_enteros()
    {
        var limiter = CreateLimiter();
        ConsumeAll(limiter, "ip-1", 4);

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, limiter.RemoveIdle());

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, limiter.RemoveIdle());
    }

    [Fact]
    public void Despues_de_borrar_un_balde_el_cliente_tiene_exactamente_lo_mismo_que_si_no_se_hubiera_borrado()
    {
        var limiter = CreateLimiter();
        ConsumeAll(limiter, "ip-1", 4);
        _time.Advance(TimeSpan.FromMinutes(1));
        limiter.RemoveIdle();

        ConsumeAll(limiter, "ip-1", 4);
        Assert.False(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void Si_el_reloj_se_atrasa_y_vuelve_no_regala_tokens()
    {
        var clock = new ManualTimeProvider();
        var limiter = new TokenBucketRateLimiter(4, TimeSpan.FromMinutes(1), clock);
        ConsumeAll(limiter, "ip-1", 4);

        clock.Now = clock.Now.AddMinutes(-1);
        Assert.False(limiter.TryAcquire("ip-1").Allowed);

        // Back to the time the bucket was emptied: still no tokens.
        clock.Now = clock.Now.AddMinutes(1);
        Assert.False(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void Parametros_invalidos_se_rechazan_al_construir()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketRateLimiter(0, TimeSpan.FromMinutes(1), _time));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketRateLimiter(5, TimeSpan.Zero, _time));
    }

    private static void ConsumeAll(IRateLimiter limiter, string clientKey, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Assert.True(limiter.TryAcquire(clientKey).Allowed);
        }
    }
}
