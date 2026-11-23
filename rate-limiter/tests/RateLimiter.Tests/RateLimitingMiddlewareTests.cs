using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace RateLimiter.Tests;

// End-to-end tests of the middleware against the real app, plus startup validation.
// Under TestServer the remote IP is null, so every request shares the "unknown" key.
public sealed class RateLimitingMiddlewareTests
{
    private static readonly Dictionary<string, string?> TestRules = new()
    {
        ["RateLimiting:Rules:login:Algorithm"] = "TokenBucket",
        ["RateLimiting:Rules:login:Limit"] = "3",
        ["RateLimiting:Rules:login:Window"] = "00:01:00",
        ["RateLimiting:Rules:orders:Algorithm"] = "SlidingWindowCounter",
        ["RateLimiting:Rules:orders:Limit"] = "2",
        ["RateLimiting:Rules:orders:Window"] = "00:01:00",
    };

    // Replaces all configuration (appsettings.json included) so tests do not depend on it.
    private static WebApplicationFactory<Program> CreateApp(
        TimeProvider time, Dictionary<string, string?>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.Sources.Clear();
                config.AddInMemoryCollection(settings ?? TestRules);
            });
            builder.ConfigureTestServices(services => services.AddSingleton(time));
        });

    [Fact]
    public async Task Login_deja_pasar_hasta_el_limite_con_headers_y_despues_devuelve_429()
    {
        var time = new FakeTimeProvider();
        using var app = CreateApp(time);
        using var client = app.CreateClient();

        for (int expectedRemaining = 2; expectedRemaining >= 0; expectedRemaining--)
        {
            HttpResponseMessage ok = await client.PostAsync("/api/login", content: null);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("3", Header(ok, "X-Ratelimit-Limit"));
            Assert.Equal(expectedRemaining.ToString(), Header(ok, "X-Ratelimit-Remaining"));
            Assert.False(ok.Headers.Contains("X-Ratelimit-Retry-After"));
        }

        HttpResponseMessage limited = await client.PostAsync("/api/login", content: null);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("3", Header(limited, "X-Ratelimit-Limit"));
        Assert.Equal("0", Header(limited, "X-Ratelimit-Remaining"));
        Assert.Equal("20", Header(limited, "X-Ratelimit-Retry-After")); // 3 per minute: 1 token every 20 s

        Assert.Equal("application/json", limited.Content.Headers.ContentType?.MediaType);
        string body = await limited.Content.ReadAsStringAsync();
        Assert.Contains("\"retryAfterSeconds\":20", body);
        Assert.Contains("\"rule\":\"login\"", body);
    }

    [Fact]
    public async Task Despues_de_esperar_el_Retry_After_vuelve_a_pasar()
    {
        var time = new FakeTimeProvider();
        using var app = CreateApp(time);
        using var client = app.CreateClient();
        for (int i = 0; i < 3; i++)
        {
            await client.PostAsync("/api/login", content: null);
        }

        HttpResponseMessage limited = await client.PostAsync("/api/login", content: null);
        int retryAfterSeconds = int.Parse(Header(limited, "X-Ratelimit-Retry-After"));

        time.Advance(TimeSpan.FromSeconds(retryAfterSeconds));

        HttpResponseMessage again = await client.PostAsync("/api/login", content: null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Retry_After_se_redondea_para_arriba()
    {
        // Window filled at a whole minute: the algorithm asks for 60 s + 1 tick, which rounds up to 61.
        var time = new FakeTimeProvider(); // starts at 2000-01-01 00:00:00
        using var app = CreateApp(time);
        using var client = app.CreateClient();
        await client.GetAsync("/api/orders");
        await client.GetAsync("/api/orders");

        HttpResponseMessage limited = await client.GetAsync("/api/orders");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("61", Header(limited, "X-Ratelimit-Retry-After"));
    }

    [Fact]
    public async Task RateLimit_en_el_controller_aplica_a_todos_sus_endpoints_y_comparten_contadores()
    {
        using var app = CreateApp(new FakeTimeProvider());
        using var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/orders/42")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/orders/7")).StatusCode);
    }

    [Fact]
    public async Task Cada_regla_cuenta_por_separado()
    {
        using var app = CreateApp(new FakeTimeProvider());
        using var client = app.CreateClient();
        await client.GetAsync("/api/orders");
        await client.GetAsync("/api/orders");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/orders")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/login", content: null)).StatusCode);
    }

    [Fact]
    public async Task Un_endpoint_sin_RateLimit_no_se_limita_ni_lleva_headers()
    {
        using var app = CreateApp(new FakeTimeProvider());
        using var client = app.CreateClient();

        for (int i = 0; i < 20; i++)
        {
            HttpResponseMessage response = await client.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("X-Ratelimit-Limit"));
        }
    }

    [Fact]
    public async Task Fail_open_si_el_limiter_falla_el_request_pasa()
    {
        using var app = CreateApp(new ThrowingTimeProvider());
        using var client = app.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/login", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Ratelimit-Limit"));
    }

    [Fact]
    public void Una_regla_invalida_en_la_config_hace_que_la_app_no_arranque()
    {
        using var app = CreateApp(new FakeTimeProvider(), new Dictionary<string, string?>(TestRules)
        {
            ["RateLimiting:Rules:login:Algorithm"] = "LeakyBucket",
        });

        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("LeakyBucket", error.ToString());
    }

    [Fact]
    public void Un_valor_mal_escrito_en_una_regla_da_un_error_que_dice_cual_es()
    {
        // When all rules were bound at once, this value made the rule silently disappear and the error
        // claimed "login" did not exist (see Program.cs).
        using var app = CreateApp(new FakeTimeProvider(), new Dictionary<string, string?>(TestRules)
        {
            ["RateLimiting:Rules:login:Window"] = "abc",
        });

        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("RateLimiting:Rules:login:Window", error.ToString());
    }

    [Fact]
    public void Un_CleanupInterval_invalido_hace_que_la_app_no_arranque()
    {
        using var app = CreateApp(new FakeTimeProvider(), new Dictionary<string, string?>(TestRules)
        {
            ["RateLimiting:CleanupInterval"] = "00:00:00",
        });

        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("CleanupInterval", error.ToString());
    }

    [Fact]
    public void Si_un_endpoint_usa_una_regla_que_no_existe_la_app_no_arranca()
    {
        // OrdersController uses [RateLimit("orders")], which this config does not define: same as a typo.
        var onlyLogin = TestRules
            .Where(setting => setting.Key.StartsWith("RateLimiting:Rules:login:"))
            .ToDictionary(setting => setting.Key, setting => setting.Value);
        using var app = CreateApp(new FakeTimeProvider(), onlyLogin);

        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("orders", error.ToString());
    }

    private static string Header(HttpResponseMessage response, string name) => response.Headers.GetValues(name).First();

    // Makes TryAcquire throw, to test fail open.
    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Broken clock (fail-open test).");
    }
}
