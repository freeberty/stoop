using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Intersect;
using Intersect.Api.Auth;
using Intersect.Api.Hubs;
using Intersect.Api.Infrastructure;
using Intersect.Common;
using Intersect.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------------------------------------------------------------- options

builder.Services.Configure<AuthOptions>(config.GetSection(AuthOptions.SectionName));
builder.Services.Configure<ChatOptions>(config.GetSection(ChatOptions.SectionName));
builder.Services.Configure<DiscoveryOptions>(config.GetSection(DiscoveryOptions.SectionName));

// The signing secret must never be a shipped default. In Development a random per-run secret is fine.
builder.Services.PostConfigure<AuthOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.JwtSecret) || o.JwtSecret.Length < 32)
    {
        if (!builder.Environment.IsDevelopment())
            throw new InvalidOperationException("Auth:JwtSecret (env Auth__JwtSecret) must be set to at least 32 characters.");
        o.JwtSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    }
});

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024);

// ---------------------------------------------------------------- services

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<IntersectRealtimeMarker>();
builder.Services.AddSingleton<Intersect.Abstractions.IRealtimeNotifier, SignalRRealtimeNotifier>(); // before AddIntersectApplication
builder.Services.AddIntersectApplication();

var provider = config["Persistence:Provider"] ?? "Postgres";
if (provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddInMemoryPersistence();
}
else
{
#if OFFLINE
    throw new InvalidOperationException("This offline build only supports Persistence:Provider=InMemory.");
#else
    var cs = config.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default is required for the Postgres provider.");
    Intersect.Infrastructure.InfrastructureExtensions.AddPostgresPersistence(builder.Services, cs);
#endif
}

#if !OFFLINE
// Nostr signature verification: vetted library by default; Nostr:Verifier=Managed selects the built-in fallback.
if (!string.Equals(config["Nostr:Verifier"], "Managed", StringComparison.OrdinalIgnoreCase))
    Intersect.Infrastructure.InfrastructureExtensions.AddNostrLibraryVerifier(builder.Services);
#endif

builder.Services.AddAuthentication(AuthDefaults.Scheme).AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, IntersectAuthHandler>(AuthDefaults.Scheme, null);
builder.Services.AddAuthorization();

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});
builder.Services.AddProblemDetails();

builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 32 * 1024;
    o.AddFilter(new AppExceptionHubFilter());
}).AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var origins = config.GetSection("Cors:Origins").Get<string[]>() ?? [];
    if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

var permit = config.GetValue("RateLimiting:PermitPerMinute", 240);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) => new ValueTask(ProblemDetailsMiddleware.WriteProblemAsync(ctx.HttpContext,
        StatusCodes.Status429TooManyRequests, "rate_limited", "RateLimited", "Too many requests. Slow down and retry shortly.", null));
    // Behind Cloudflare and Fly the socket address is the proxy's, so every visitor would share one bucket.
    // Prefer the client IP the proxies forward: CF-Connecting-IP (Cloudflare), then Fly-Client-IP (set by Fly's edge).
    static string Key(HttpContext c)
    {
        var h = c.Request.Headers;
        var ip = h["CF-Connecting-IP"].ToString();
        if (string.IsNullOrWhiteSpace(ip)) ip = h["Fly-Client-IP"].ToString();
        return string.IsNullOrWhiteSpace(ip) ? c.Connection.RemoteIpAddress?.ToString() ?? "unknown" : ip.Trim();
    }
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetSlidingWindowLimiter(
        Key(c), _ => new SlidingWindowRateLimiterOptions { PermitLimit = permit, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 }));
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(
        "auth:" + Key(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimiting:AuthPerMinute", 20), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddHostedService<LifecycleWorker>();

#if !OFFLINE
builder.Services.AddOpenApi();
#endif

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// ---------------------------------------------------------------- startup tasks

if (!provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
#if !OFFLINE
    await Intersect.Infrastructure.InfrastructureExtensions.MigrateAsync(app.Services);
#endif
}
if (config.GetValue("Seed:Enabled", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    var added = await scope.ServiceProvider.GetRequiredService<SeedService>().SeedIfEmptyAsync();
    if (added > 0) app.Logger.LogInformation("Seeded {Count} synthetic communities", added);
}

// ---------------------------------------------------------------- pipeline

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts(); // TLS is terminated by the reverse proxy / hosting platform

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["X-Frame-Options"] = "DENY";
    await next();
});

app.UseMiddleware<ProblemDetailsMiddleware>();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", async (IServiceProvider sp) =>
{
    try
    {
        await using var scope = sp.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Intersect.Abstractions.ICommunityRepository>().CountAsync();
        return Results.Ok(new { status = "healthy", time = DateTimeOffset.UtcNow });
    }
    catch (Exception)
    {
        return Results.Json(new { status = "unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous().DisableRateLimiting();

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

#if !OFFLINE
app.MapOpenApi();
app.MapGet("/docs", () => Results.Content(
    """
    <!doctype html><html><head><meta charset="utf-8"><title>Intersect API</title>
    <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5/swagger-ui.css"></head>
    <body><div id="ui"></div>
    <script src="https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
    <script>SwaggerUIBundle({ url: '/openapi/v1.json', dom_id: '#ui' });</script></body></html>
    """, "text/html")).AllowAnonymous();
#endif

app.Run();

/// <summary>Marker so the realtime notifier registration is easy to find.</summary>
public sealed class IntersectRealtimeMarker { }

public partial class Program { }
