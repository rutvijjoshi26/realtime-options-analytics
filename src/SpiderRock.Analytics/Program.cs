using System.Text.Json;
using System.Text.Json.Serialization;
using SpiderRock.Analytics;
using SpiderRock.Analytics.Api;
using SpiderRock.Analytics.Feed;
using SpiderRock.Analytics.Model;
using SpiderRock.Analytics.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var analyticsOptions = new AnalyticsOptions
{
    RiskFreeRate = builder.Configuration.GetValue("Analytics:RiskFreeRate", 0.045),
    DividendYield = builder.Configuration.GetValue("Analytics:DividendYield", 0.0)
};

var feedOptions = new FeedOptions
{
    Port = builder.Configuration.GetValue("Feed:Port", 9301),
    BindAddress = builder.Configuration.GetValue("Feed:BindAddress", "127.0.0.1")!,
    MulticastGroup = builder.Configuration.GetValue<string?>("Feed:MulticastGroup", null),
    LoopInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue("Feed:LoopIntervalSeconds", 5.0))
};

builder.Services.AddSingleton(analyticsOptions);
builder.Services.AddSingleton(feedOptions);
builder.Services.AddSingleton<AnalyticsEngine>(sp => new AnalyticsEngine(sp.GetRequiredService<AnalyticsOptions>()));
builder.Services.AddSingleton<LiveAnalyticsHub>();
builder.Services.AddSingleton<UdpQuoteFeed>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UdpQuoteFeed>());

var connectionString = builder.Configuration.GetConnectionString("MySql");
if (!string.IsNullOrWhiteSpace(connectionString))
    builder.Services.AddSingleton(new CloseSnapshotRepository(connectionString));

var app = builder.Build();
app.UseWebSockets();

// Wire the engine's publish event into the WebSocket fan-out.
var engine = app.Services.GetRequiredService<AnalyticsEngine>();
var hub = app.Services.GetRequiredService<LiveAnalyticsHub>();
engine.AnalyticPublished += a => hub.Publish(a);

// ---------------------------------------------------------------------------
// REST surface
// ---------------------------------------------------------------------------

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

/// Live analytics for one underlying, or the whole book when no name is given.
app.MapGet("/api/analytics", (AnalyticsEngine eng, string? underlying) =>
{
    var data = string.IsNullOrWhiteSpace(underlying)
        ? eng.Snapshot()
        : eng.SnapshotFor(underlying.ToUpperInvariant());

    return Results.Ok(data.Select(Wire.From));
});

/// A single contract, addressed by its full option key.
app.MapGet("/api/analytics/{underlying}/{expiry}/{strike}/{right}",
    (AnalyticsEngine eng, string underlying, string expiry, decimal strike, string right) =>
{
    if (!DateOnly.TryParse(expiry, out var exp))
        return Results.BadRequest(new { error = "expiry must be yyyy-MM-dd" });

    var r = right.ToUpperInvariant() switch
    {
        "C" or "CALL" => OptionRight.Call,
        "P" or "PUT" => OptionRight.Put,
        _ => (OptionRight?)null
    };
    if (r is null) return Results.BadRequest(new { error = "right must be C or P" });

    var key = new OptionKey(underlying.ToUpperInvariant(), exp, strike, r.Value);
    return eng.TryGetAnalytic(key, out var a)
        ? Results.Ok(Wire.From(a))
        : Results.NotFound(new { error = "no analytic for contract", contract = key.ToString() });
});

/// The fitted volatility surface, refit on demand.
app.MapGet("/api/surface/{underlying}", (AnalyticsEngine eng, string underlying, bool refit = false) =>
{
    var name = underlying.ToUpperInvariant();
    var smiles = refit ? eng.RefitSurface(name) : eng.GetSurface(name);

    if (smiles.Count == 0)
        return Results.NotFound(new { error = "no surface for underlying", underlying = name });

    return Results.Ok(smiles.Select(s => new
    {
        expiry = s.Expiry.ToString("yyyy-MM-dd"),
        yearsToExpiry = s.YearsToExpiry,
        forward = s.Forward,
        atmVol = s.AtmVol,
        skew = s.Skew,
        curvature = s.C,
        pointCount = s.PointCount,
        rmsError = s.RmsError,
        arbitrageFree = s.IsArbitrageFree,
        arbitrageNote = s.ArbitrageNote
    }));
});

/// Feed and engine health counters: the first thing to look at when a client
/// reports missing or stale analytics.
app.MapGet("/api/feed/stats", (UdpQuoteFeed feed, AnalyticsEngine eng, LiveAnalyticsHub h) =>
{
    var s = feed.Statistics.Snapshot();
    return Results.Ok(new
    {
        feed = new
        {
            s.Received,
            s.DecodeFailures,
            s.RejectedQuotes,
            s.GapCount,
            s.GapMessages,
            s.OutOfOrder,
            s.LastSequence
        },
        engine = new
        {
            computed = eng.ComputedCount,
            solverFailures = eng.SolverFailureCount,
            trackedContracts = eng.TrackedContracts
        },
        websocket = new { subscribers = h.SubscriberCount, dropped = h.TotalDropped }
    });
});

/// Persists the current book as the CLOSE snapshot for a session.
app.MapPost("/api/close", async (AnalyticsEngine eng, IServiceProvider sp, string? sessionDate) =>
{
    var repo = sp.GetService<CloseSnapshotRepository>();
    if (repo is null)
        return Results.Problem("MySQL connection string not configured", statusCode: 503);

    var date = string.IsNullOrWhiteSpace(sessionDate)
        ? DateOnly.FromDateTime(DateTime.UtcNow)
        : DateOnly.Parse(sessionDate);

    var snapshot = eng.Snapshot();
    int rows = await repo.SaveAnalyticsAsync(date, snapshot);

    int surfaceRows = 0;
    foreach (var name in eng.Underlyings())
        surfaceRows += await repo.SaveSurfaceAsync(date, name, eng.RefitSurface(name));

    return Results.Ok(new { sessionDate = date.ToString("yyyy-MM-dd"), contracts = snapshot.Count, rows, surfaceRows });
});

/// Historical chain query out of MySQL.
app.MapGet("/api/history/{underlying}", async (IServiceProvider sp, string underlying, string sessionDate) =>
{
    var repo = sp.GetService<CloseSnapshotRepository>();
    if (repo is null)
        return Results.Problem("MySQL connection string not configured", statusCode: 503);

    if (!DateOnly.TryParse(sessionDate, out var date))
        return Results.BadRequest(new { error = "sessionDate must be yyyy-MM-dd" });

    var rows = await repo.QueryChainAsync(underlying.ToUpperInvariant(), date);
    return Results.Ok(rows.Select(Wire.From));
});

/// Live streaming endpoint. Mirrors a WebSocket market data API: connect,
/// optionally filter by underlying, receive analytics as they are computed.
app.Map("/ws/analytics", async (HttpContext ctx, LiveAnalyticsHub h) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("expected a WebSocket upgrade request");
        return;
    }

    var filter = ctx.Request.Query["underlying"]
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(v => v!.ToUpperInvariant())
        .ToArray();

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await h.RunClientAsync(socket, filter, ctx.RequestAborted);
});

app.Run();

internal static class Wire
{
    public static object From(OptionAnalytic a) => new
    {
        underlying = a.Key.Underlying,
        expiry = a.Key.Expiry.ToString("yyyy-MM-dd"),
        strike = a.Key.Strike,
        right = a.Key.Right == OptionRight.Call ? "C" : "P",
        underlyingPrice = a.UnderlyingPrice,
        yearsToExpiry = a.YearsToExpiry,
        impliedVol = a.ImpliedVol,
        delta = a.Delta,
        gamma = a.Gamma,
        vega = a.Vega,
        theta = a.Theta,
        rho = a.Rho,
        reason = a.Reason.ToString().ToUpperInvariant(),
        sequenceNumber = a.SequenceNumber,
        timestamp = a.TimestampUtc
    };
}

/// Exposed so the integration tests can drive the host through WebApplicationFactory.
public partial class Program { }
