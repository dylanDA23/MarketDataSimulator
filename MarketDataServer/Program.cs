// File: MarketDataServer/Program.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using MarketDataServer.Data;
using MarketDataServer.Sim;
using MarketDataServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// Console logging 
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Postgres DB 
var serverConn = builder.Configuration["Server:Postgres:Connection"]
                 ?? Environment.GetEnvironmentVariable("SERVER_POSTGRES_CONN")
                 ?? "Host=postgres_server;Port=5432;Username=postgres;Password=postgres;Database=marketdb";

builder.Services.AddDbContext<ServerPersistenceDbContext>(opts =>
    opts.UseNpgsql(serverConn));

// Named HttpClient for Binance REST calls 
builder.Services.AddHttpClient("binance", c =>
{
    c.BaseAddress = new Uri("https://api.binance.com/");
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MarketDataServer-BinanceLiveFeed/1.0");
});

// Choose feed mode by env var MARKET_FEED_MODE or MarketFeed:Mode config
var feedMode = builder.Configuration["MarketFeed:Mode"]
               ?? Environment.GetEnvironmentVariable("MARKET_FEED_MODE")
               ?? "Simulation";

if (feedMode.Equals("Live", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IMarketDataFeed, BinanceLiveFeed>();
}
else
{
    builder.Services.AddSingleton<IMarketDataFeed, SimulationFeed>();
}

// Other services
builder.Services.AddSingleton<OrderBookManager>();
builder.Services.AddGrpc();

var app = builder.Build();

// Ensure DB schema applied at startup (safe: uses service scope)
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<ServerPersistenceDbContext>();
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Database migrate failed (continuing): {Message}", ex.Message);
    }
}

var obMgr = app.Services.GetRequiredService<OrderBookManager>();
// Start the feed manager tied to the application's stopping token (existing behavior).
_ = obMgr.StartAsync(app.Lifetime.ApplicationStopping);

// Map gRPC service and a plain HTTP root for quick checks
app.MapGrpcService<MarketDataService>();
app.MapGet("/", (ILogger<Program> logger) =>
{
    logger.LogInformation("Root endpoint requested. FeedMode={FeedMode}", feedMode);
    return Results.Text($"MarketDataServer (gRPC) running. FeedMode={feedMode}", "text/plain");
});

// Top-level CancellationTokenSource so Ctrl+C / SIGINT can cancel startup and trigger graceful shutdown.
using var topLevelCts = new CancellationTokenSource();

// Wire Ctrl+C (SIGINT) to request graceful shutdown.
// e.Cancel = true prevents the process from being terminated immediately by the runtime, allowing a graceful shutdown.
Console.CancelKeyPress += (sender, e) =>
{
    try
    {
        e.Cancel = true; // prevent abrupt termination; allow graceful shutdown
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Shutdown requested (Ctrl+C) - initiating graceful shutdown.");
    }
    catch
    {
        // ignore if logger can't be acquired
    }

    // Cancel the top-level token and request application stop so hosted services get ApplicationStopping.
    topLevelCts.Cancel();
    try { app.Lifetime.StopApplication(); } catch { }
};

try
{
    // Start the web app and wait for shutdown or Ctrl+C (topLevelCts.Token)
    await app.StartAsync(topLevelCts.Token);

    var startLogger = app.Services.GetRequiredService<ILogger<Program>>();
    startLogger.LogInformation("MarketDataServer started. Press Ctrl+C to shut down.");

    // Wait for shutdown. Passing the topLevelCts.Token means Ctrl+C cancels this wait.
    await app.WaitForShutdownAsync(topLevelCts.Token);
}
catch (OperationCanceledException)
{
    // expected when Ctrl+C is pressed - fall through to shutdown cleanup
}
finally
{
    // Ensure a tidy shutdown with a bounded timeout
    using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try
    {
        await app.StopAsync(shutdownCts.Token);
    }
    catch (OperationCanceledException)
    {
        // timed out during shutdown
    }
    catch
    {
        // ignore other errors during shutdown
    }
}
