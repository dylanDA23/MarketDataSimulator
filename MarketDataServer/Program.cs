using MarketDataServer.Data;
using MarketDataServer.Sim;
using MarketDataServer.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Configure Postgres connection for server DB (use env or default)
var serverConn = builder.Configuration["Server:Postgres:Connection"]
                 ?? Environment.GetEnvironmentVariable("SERVER_POSTGRES_CONN")
                 ?? "Host=postgres_server;Port=5432;Username=postgres;Password=postgres;Database=marketdb";

builder.Services.AddDbContext<ServerPersistenceDbContext>(opts =>
    opts.UseNpgsql(serverConn));

// Choose feed mode by env var MARKET_FEED_MODE
var feedMode = builder.Configuration["MarketFeed:Mode"] ?? Environment.GetEnvironmentVariable("MARKET_FEED_MODE") ?? "Simulation";
if (feedMode.Equals("Live", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient();
    // Note: Live feed implementation (Binance) not included in this minimal copy.
    // You can implement BinanceLiveFeed that implements IMarketDataFeed and register it here.
    builder.Services.AddSingleton<IMarketDataFeed, SimulationFeed>(); // fallback
}
else
{
    builder.Services.AddSingleton<IMarketDataFeed, SimulationFeed>();
}

builder.Services.AddSingleton<OrderBookManager>();
builder.Services.AddGrpc();

var app = builder.Build();

// Ensure DB schema applied
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ServerPersistenceDbContext>();
    db.Database.Migrate();
}

// Start feed/manager
var obMgr = app.Services.GetRequiredService<OrderBookManager>();
_ = obMgr.StartAsync(app.Lifetime.ApplicationStopping);

app.MapGrpcService<MarketDataService>();
app.MapGet("/", () => "MarketDataServer (gRPC) running");

app.Run();
