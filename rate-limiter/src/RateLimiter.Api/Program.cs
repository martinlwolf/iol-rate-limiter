using RateLimiter.Api.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Injected so tests can control time with FakeTimeProvider.
builder.Services.AddSingleton(TimeProvider.System);

// Rules are bound one by one rather than with Get<Dictionary<...>>(): that way a rule with a malformed value
// (e.g. "Window": "abc") fails with an error naming that value, instead of being silently dropped and ending
// up as a misleading "rule does not exist".
builder.Services.AddSingleton(services => new RateLimitRules(
    services.GetRequiredService<IConfiguration>()
        .GetSection("RateLimiting:Rules")
        .GetChildren()
        .ToDictionary(rule => rule.Key, rule => rule.Get<RateLimitRuleOptions>() ?? new RateLimitRuleOptions()),
    services.GetRequiredService<TimeProvider>()));

builder.Services.AddHostedService<IdleBucketCleanupService>();

var app = builder.Build();

// The middleware goes after UseRouting because it needs the resolved endpoint to read its [RateLimit].
app.UseRouting();
app.UseMiddleware<RateLimitingMiddleware>();
app.MapControllers();

// Fail fast: building RateLimitRules validates the config, then every [RateLimit] is checked against the
// configured rules. These are the same endpoints whose metadata the middleware reads.
IEnumerable<string> usedRuleNames = ((IEndpointRouteBuilder)app).DataSources
    .SelectMany(dataSource => dataSource.Endpoints)
    .Select(endpoint => endpoint.Metadata.GetMetadata<RateLimitAttribute>()?.RuleName)
    .OfType<string>();
app.Services.GetRequiredService<RateLimitRules>().EnsureRulesExist(usedRuleNames);

app.Run();
