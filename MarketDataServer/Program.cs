using System;
using MarketDataServer.Data;
using MarketDataServer.Sim;
using MarketDataServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// -------------------------------
// Postgres DB (server)
var serverConn = builder.Configuration["Server:Postgres:Connection"]
                 ?? Environment.GetEnvironmentVariable("SERVER_POSTGRES_CONN")
                 ?? "Host=postgres_server;Port=5432;Username=postgres;Password=postgres;Database=marketdb";

builder.Services.AddDbContext<ServerPersistenceDbContext>(opts =>
    opts.UseNpgsql(serverConn));

// -------------------------------
// Named HttpClient for Binance REST calls (used by BinanceLiveFeed)
// Place this BEFORE registering BinanceLiveFeed so the feed can request the named client.
builder.Services.AddHttpClient("binance", c =>
{
    c.BaseAddress = new Uri("https://api.binance.com/");
    c.Timeout = TimeSpan.FromSeconds(10);
    // helpful to identify requests in logs; not required
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MarketDataServer-BinanceLiveFeed/1.0");
});

// -------------------------------
// Choose feed mode by env var MARKET_FEED_MODE or MarketFeed:Mode config
var feedMode = builder.Configuration["MarketFeed:Mode"]
               ?? Environment.GetEnvironmentVariable("MARKET_FEED_MODE")
               ?? "Simulation";

if (feedMode.Equals("Live", StringComparison.OrdinalIgnoreCase))
{
    // Live mode -> use BinanceLiveFeed which consumes the named "binance" HttpClient
    builder.Services.AddSingleton<IMarketDataFeed, BinanceLiveFeed>();
}
else
{
    // Simulation fallback
    builder.Services.AddSingleton<IMarketDataFeed, SimulationFeed>();
}

// -------------------------------
// Other services
builder.Services.AddSingleton<OrderBookManager>();
builder.Services.AddGrpc();

var app = builder.Build();

// Ensure DB schema applied at startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ServerPersistenceDbContext>();
    db.Database.Migrate();
}

// Start the OrderBookManager / feed
var obMgr = app.Services.GetRequiredService<OrderBookManager>();
_ = obMgr.StartAsync(app.Lifetime.ApplicationStopping);

app.MapGrpcService<MarketDataService>();
app.MapGet("/", () => $"MarketDataServer (gRPC) running. FeedMode={feedMode}");

app.Run();
