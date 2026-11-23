using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core;

namespace RateLimiter.Tests;

// 1-minute windows. The clock starts at a whole minute so the examples match the book.
public sealed class SlidingWindowCounterRateLimiterTests
{
    private static readonly DateTimeOffset OneOClock = new(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(OneOClock);

    private SlidingWindowCounterRateLimiter CreateLimiter(int limit) =>
        new(limit, TimeSpan.FromMinutes(1), _time);

    [Fact]
    public void Ejemplo_del_libro_3_mas_5_por_0_7_da_6_5_y_se_redondea_a_6()
    {
        // Fig. 4-11: limit 7, 5 requests in the previous minute and 3 in the current one, 30% into the minute.
        var limiter = CreateLimiter(limit: 7);
        ConsumeAll(limiter, "ip-1", 5);

        _time.SetUtcNow(OneOClock.AddSeconds(78));
        ConsumeAll(limiter, "ip-1", 3);

        // 3 + 5 × 0.7 = 6.5 → 6 < 7: allowed.
        RateLimitDecision decision = limiter.TryAcquire("ip-1");
        Assert.True(decision.Allowed);
        Assert.Equal(0, decision.Remaining);

        // 4 + 3.5 = 7.5 → 7: rejected.
        Assert.False(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void En_la_primera_ventana_pasan_exactamente_limit_requests()
    {
        var limiter = CreateLimiter(limit: 5);

        for (int expectedRemaining = 4; expectedRemaining >= 0; expectedRemaining--)
        {
            RateLimitDecision decision = limiter.TryAcquire("ip-1");
            Assert.True(decision.Allowed);
            Assert.Equal(5, decision.Limit);
            Assert.Equal(expectedRemaining, decision.Remaining);
        }

        Assert.False(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void Corrige_la_rafaga_en_el_borde_de_la_ventana_del_fixed_window()
    {
        // Fig. 4-9: a fixed window would allow 5 at the end of one minute and 5 more at the start of the next.
        var limiter = CreateLimiter(limit: 5);

        _time.SetUtcNow(OneOClock.AddSeconds(50));
        ConsumeAll(limiter, "ip-1", 5);

        // 5 s into the next minute the previous one weighs 5 × 0.917 = 4.58: only one more fits.
        _time.SetUtcNow(OneOClock.AddSeconds(65));
        Assert.True(limiter.TryAcquire("ip-1").Allowed);
        Assert.False(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void El_peso_de_la_ventana_anterior_baja_a_medida_que_avanza_la_actual()
    {
        var limiter = CreateLimiter(limit: 10);
        ConsumeAll(limiter, "ip-1", 10);

        // Halfway through the next minute: 10 × 0.5 = 5, so 5 more fit.
        _time.SetUtcNow(OneOClock.AddSeconds(90));
        ConsumeAll(limiter, "ip-1", 5);
        Assert.False(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void Si_pasa_una_ventana_entera_sin_requests_la_anterior_no_cuenta()
    {
        var limiter = CreateLimiter(limit: 5);
        ConsumeAll(limiter, "ip-1", 5);

        _time.SetUtcNow(OneOClock.AddMinutes(2));
        ConsumeAll(limiter, "ip-1", 5);
        Assert.False(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void RetryAfter_dentro_de_la_misma_ventana()
    {
        // At 1:01:07.5 the previous minute weighs 4 × 0.875 = 3.5, so 7 fit and the 8th is rejected.
        var limiter = CreateLimiter(limit: 10);
        ConsumeAll(limiter, "ip-1", 4);
        _time.SetUtcNow(OneOClock.AddSeconds(67.5));
        ConsumeAll(limiter, "ip-1", 7);

        RateLimitDecision rejected = limiter.TryAcquire("ip-1");
        Assert.False(rejected.Allowed);
        // 7 + 4 × (1 − f) < 10 once f > 0.25 (1:01:15): 7.5 s plus 1 tick, because at that exact instant
        // the estimate is exactly 10.
        Assert.Equal(TimeSpan.FromSeconds(7.5) + TimeSpan.FromTicks(1), rejected.RetryAfter);

        _time.Advance(rejected.RetryAfter);
        Assert.True(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void RetryAfter_cuando_hay_que_esperar_a_la_ventana_siguiente()
    {
        var limiter = CreateLimiter(limit: 3);
        ConsumeAll(limiter, "ip-1", 3);

        RateLimitDecision rejected = limiter.TryAcquire("ip-1");
        Assert.False(rejected.Allowed);
        // At exactly 1:01:00 the previous minute still weighs 3 × 1 = 3: one more tick is needed.
        Assert.Equal(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1), rejected.RetryAfter);

        _time.Advance(rejected.RetryAfter);
        Assert.True(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void Los_rechazados_no_cuentan()
    {
        var limiter = CreateLimiter(limit: 2);
        ConsumeAll(limiter, "ip-1", 2);
        for (int i = 0; i < 50; i++)
        {
            Assert.False(limiter.TryAcquire("ip-1").Allowed);
        }

        // If rejections counted, the previous minute would weigh 52 × 0.5 and nothing would pass.
        _time.SetUtcNow(OneOClock.AddSeconds(90));
        Assert.True(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void RemoveIdle_borra_solo_despues_de_dos_ventanas_sin_uso()
    {
        var limiter = CreateLimiter(limit: 5);
        ConsumeAll(limiter, "ip-1", 5);

        _time.SetUtcNow(OneOClock.AddSeconds(119)); // the previous window still counts
        Assert.Equal(0, limiter.RemoveIdle());

        _time.SetUtcNow(OneOClock.AddMinutes(2));
        Assert.Equal(1, limiter.RemoveIdle());
    }

    [Fact]
    public void Si_el_reloj_se_atrasa_y_vuelve_no_regala_requests()
    {
        var clock = new ManualTimeProvider();
        var limiter = new SlidingWindowCounterRateLimiter(5, TimeSpan.FromMinutes(1), clock);
        ConsumeAll(limiter, "ip-1", 5);

        clock.Now = clock.Now.AddMinutes(-1);
        Assert.False(limiter.TryAcquire("ip-1").Allowed);

        clock.Now = clock.Now.AddMinutes(1);
        Assert.False(limiter.TryAcquire("ip-1").Allowed);
    }

    [Fact]
    public void Cada_cliente_tiene_sus_propios_contadores()
    {
        var limiter = CreateLimiter(limit: 2);
        ConsumeAll(limiter, "ip-1", 2);

        Assert.False(limiter.TryAcquire("ip-1").Allowed);
        Assert.True(limiter.TryAcquire("ip-2").Allowed);
    }

    [Fact]
    public void Parametros_invalidos_se_rechazan_al_construir()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowCounterRateLimiter(0, TimeSpan.FromMinutes(1), _time));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowCounterRateLimiter(5, TimeSpan.Zero, _time));
    }

    private static void ConsumeAll(IRateLimiter limiter, string clientKey, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Assert.True(limiter.TryAcquire(clientKey).Allowed);
        }
    }
}
