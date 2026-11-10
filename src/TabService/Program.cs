using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Npgsql;
using TabService;
using TabService.Data;
using TabService.Hubs;
using TabService.Live;
using TabService.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TabOptions>(builder.Configuration.GetSection(TabOptions.Section));
var tab = builder.Configuration.GetSection(TabOptions.Section).Get<TabOptions>() ?? new TabOptions();
if (tab.ApiKey.Length < 16)
    throw new InvalidOperationException("Tab:ApiKey (env Tab__ApiKey) must be set to a random string of at least 16 characters.");
if (tab.AdminKey.Length is > 0 and < 32)
    throw new InvalidOperationException("Tab:AdminKey (env Tab__AdminKey) must be at least 32 characters, or empty to disable the admin dashboard.");

var connectionString = builder.Configuration.GetConnectionString("Db")
    ?? throw new InvalidOperationException("ConnectionStrings:Db (env ConnectionStrings__Db) is not set.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<SampleStore>();
builder.Services.AddSingleton<ConnectionTracker>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddHostedService<RetentionService>();

builder.Services.AddSignalR(o =>
{
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    o.MaximumReceiveMessageSize = 512 * 1024; // room for a full sample batch
});

// The container sits behind Nginx Proxy Manager, which terminates TLS. Trust its X-Forwarded-* headers
// (the API port must only be reachable from the proxy, not from the internet).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("create", ctx => PerIp(ctx, 10));
    o.AddPolicy("owner", ctx => PerIp(ctx, 30));
    o.AddPolicy("view", ctx => PerIp(ctx, 60));
    o.AddPolicy("hub", ctx => PerIp(ctx, 120));
    o.AddPolicy("admin", ctx => PerIp(ctx, 120)); // the key itself (>= 32 random chars) is what stops guessing
});

var origins = tab.AllowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (origins.Length > 0)
        p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

var app = builder.Build();

app.UseForwardedHeaders();
if (!string.IsNullOrWhiteSpace(tab.PathBase)) app.UsePathBase(tab.PathBase);
app.UseCors();
app.UseRateLimiter();

app.MapGet("/health", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "ok" });
});

var api = app.MapGroup("/api");

// Phone: start sharing. Requires the server-wide API key; returns the per-session owner token and share link.
api.MapPost("/sessions", async (HttpRequest http, CreateSessionRequest? body, SessionService service, IOptions<TabOptions> options, CancellationToken ct) =>
{
    if (!Tokens.EqualsConstantTime(http.Headers["X-Api-Key"].ToString(), options.Value.ApiKey))
        return Results.Unauthorized();
    return Results.Ok(await service.CreateAsync(body ?? new CreateSessionRequest(null, null, null), ct));
}).RequireRateLimiting("create");

// Phone: stop sharing ("revoked") or trip finished ("ended"). The link stops working immediately.
api.MapPost("/sessions/{id:guid}/close", async (Guid id, HttpRequest http, CloseSessionRequest? body, SessionStore sessions, SessionService service, CancellationToken ct) =>
{
    var session = await sessions.GetAsync(id, ct);
    if (session is null || !Tokens.Matches(BearerToken(http), session.OwnerTokenHash))
        return Results.Unauthorized();
    var reason = CloseReasons.IsClientReason(body?.Reason) ? body!.Reason! : CloseReasons.Revoked;
    await service.CloseAsync(id, reason, ct);
    return Results.NoContent();
}).RequireRateLimiting("owner");

// Viewer without a live connection: the full raw route so far.
api.MapGet("/view", async (HttpRequest http, SessionStore sessions, SampleStore samples, CancellationToken ct) =>
{
    var token = http.Headers["X-Viewer-Token"].ToString();
    if (string.IsNullOrEmpty(token)) return Results.NotFound();
    var session = await sessions.GetByViewerTokenHashAsync(Tokens.Hash(token), ct);
    if (session is null || !session.IsOpen) return Results.NotFound();
    return Results.Ok(new Snapshot(session.ToInfo(), await samples.GetAllAsync(session.Id, ct)));
}).RequireRateLimiting("view");

// Admin dashboard: every stored session, open or closed. Requires the admin key.
api.MapGet("/admin/sessions", async (HttpRequest http, SessionStore sessions, IOptions<TabOptions> options, CancellationToken ct) =>
    TripHub.IsAdmin(http.Headers["X-Admin-Key"].ToString(), options.Value)
        ? Results.Ok(await sessions.ListAllAsync(ct))
        : Results.Unauthorized())
    .RequireRateLimiting("admin");

// The domain root has no page of its own: send people to the web app.
app.MapGet("/", () => Results.Redirect("/view/"));

app.MapHub<TripHub>("/hubs/trip").RequireRateLimiting("hub");

await Database.InitializeAsync(app.Services);
app.Run();

static RateLimitPartition<string> PerIp(HttpContext ctx, int perMinute) =>
    RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) });

static string? BearerToken(HttpRequest http)
{
    var header = http.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
}
