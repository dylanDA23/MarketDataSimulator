// File: MarketDataClient/Program.cs
using System.CommandLine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using MarketDataClient.Data;
using MarketDataClient.Workers;
using Microsoft.Extensions.Logging;
using MarketDataClient.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarketDataClient
{
    internal static class Program
    {
        static async Task<int> Main(string[] args)
        {
            // Top-level cancellation source: will be cancelled by Ctrl+C
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                // Prevent the process from terminating immediately so we can shut down gracefully
                e.Cancel = true;
                AnsiConsole.MarkupLine("\n[yellow]Shutdown requested (Ctrl+C)...[/]");
                cts.Cancel();
            };

            if (args != null && args.Length > 0 &&
                (args.Any(a => a.StartsWith("--applicationName", StringComparison.OrdinalIgnoreCase))
                 || args.Any(a => a.StartsWith("--depsfile", StringComparison.OrdinalIgnoreCase))
                 || args.Any(a => a.StartsWith("--additionalProbingPath", StringComparison.OrdinalIgnoreCase))
                 || args.Any(a => a.StartsWith("--fx-version", StringComparison.OrdinalIgnoreCase))
                 || args.Any(a => a.StartsWith("--runtimeconfig", StringComparison.OrdinalIgnoreCase))
                 || args.Any(a => a.StartsWith("--additionalProbingPaths", StringComparison.OrdinalIgnoreCase))
                 || args.Any(a => a.StartsWith("--roll-forward", StringComparison.OrdinalIgnoreCase))))
            {
                return 0;
            }

            // CLI options
            var root = new RootCommand("MarketDataClient console UI (Spectre.Console + System.CommandLine)");

            var serverOption = new Option<string>(
                aliases: new[] { "--server", "-s" },
                getDefaultValue: () => Environment.GetEnvironmentVariable("MARKETDATA_SERVER_URL") ?? "http://localhost:5000",
                description: "gRPC server base URL (http://host:5000)");

            var instrumentsOption = new Option<string[]>(
                aliases: new[] { "--instruments", "-i" },
                description: "Comma-separated list of instrument symbols (BTCUSDT,ETHUSDT). You may pass multiple -i values or a single comma-separated value.",
                getDefaultValue: () => new[] { "BTCUSDT" });

            instrumentsOption.AddAlias("--symbols");

            var persistOption = new Option<bool>(
                aliases: new[] { "--persist", "-P" },
                description: "Persist incoming messages to local DB.",
                getDefaultValue: () => false);

            root.AddOption(serverOption);
            root.AddOption(instrumentsOption);
            root.AddOption(persistOption);

            // Handler
            root.SetHandler(async (string server, string[] instruments, bool persist) =>
            {
                // Normalize inputs
                var serverUrl = string.IsNullOrWhiteSpace(server)
                    ? Environment.GetEnvironmentVariable("MARKETDATA_SERVER_URL") ?? "http://localhost:5000"
                    : server;

                string[] instrumentsList;
                if (instruments == null || instruments.Length == 0)
                    instrumentsList = new[] { "BTCUSDT" };
                else
                {
                    instrumentsList = instruments
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        .Select(s => s.Trim().ToUpperInvariant())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToArray();
                    if (instrumentsList.Length == 0) instrumentsList = new[] { "BTCUSDT" };
                }

                IHost? host = null;
                try
                {
                    if (persist)
                    {
                        // read connection string from environment (the design-time factory also uses the same env var)
                        var conn = Environment.GetEnvironmentVariable("CLIENT_POSTGRES_CONN")
                                   ?? Environment.GetEnvironmentVariable("SERVER_POSTGRES_CONN")
                                   ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=marketdb";

                        // Create a host for the persister but route console logs to stderr and
                        // silence EF Core console noise from stdout (EF logs still end up in the file).
                        host = Host.CreateDefaultBuilder()
                            .ConfigureLogging(logging =>
                            {
                                // Make console logger send logs to stderr so we can redirect them to a file if desired
                                logging.ClearProviders();
                                logging.AddConsole(opts =>
                                {
                                    // route all logged messages to stderr (so stdout remains the UI)
                                    opts.LogToStandardErrorThreshold = LogLevel.Trace;
                                });

                                // Reduce EF Core console noise. They will still be written to the file if someone redirects stderr.
                                logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
                                logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

                                // Disable any console logs from the PersisterWorker class entirely so the UI is not flooded.
                                logging.AddFilter("MarketDataClient.Workers.PersisterWorker", LogLevel.None);
                            })
                            .ConfigureServices((ctx, services) =>
                            {
                                // Register the shared in-memory hub used by the PersisterWorker and the UI
                                services.AddSingleton<MarketDataService>(sp => new MarketDataService(instrumentsList));

                                services.AddDbContext<ClientPersistenceDbContext>(options =>
                                    options.UseNpgsql(conn, b => b.EnableRetryOnFailure()));

                                services.AddHostedService<PersisterWorker>();

                                services.AddHttpClient();
                            })
                            .Build();

                        // Start the host - pass the top-level token so Ctrl+C cancels startup/host
                        await host.StartAsync(cts.Token);
                        AnsiConsole.MarkupLine("[green]Persistence enabled:[/] Persister worker started.");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[yellow]Note:[/] persistence (--persist) not requested. Running UI only.");
                    }

                    // Run the console UI client which subscribes to the gRPC server
                    AnsiConsole.Clear();
                    AnsiConsole.Write(new FigletText("MarketDataClient").Centered());

                    var escServer = Markup.Escape(serverUrl);
                    var escInstruments = Markup.Escape(string.Join(',', instrumentsList));
                    try
                    {
                        AnsiConsole.MarkupLine($"[{GruvboxTheme.BrightAqua}]Server:[/] {escServer} [grey]| Instruments:[/] {escInstruments}");
                    }
                    catch
                    {
                        AnsiConsole.WriteLine($"Server: {escServer} | Instruments: {escInstruments}");
                    }

                    using var http = new System.Net.Http.HttpClient();

                    // If persisting (host != null) get the shared hub and pass it to the UI so the UI doesn't open its own gRPC connection.
                    MarketDataService? shared = host?.Services.GetService<MarketDataService>();

                    var client = new MarketDataConsoleClient(serverUrl, instrumentsList, http, persist, shared);

                    // Run UI and block here until cts.Token is cancelled (Ctrl+C) or client ends normally.
                    await client.RunAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // expected on Ctrl+C - nothing to do
                }
                catch (Exception ex)
                {
                    try
                    {
                        AnsiConsole.MarkupLine($"[red]Unhandled client error:[/] {Markup.Escape(ex.Message)}");
                        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything | ExceptionFormats.ShowLinks);
                    }
                    catch
                    {
                        Console.WriteLine("Unhandled client error: " + ex.ToString());
                    }

                    throw;
                }
                finally
                {
                    if (host != null)
                    {
                        // shutdown the host cleanly when console UI exits, bounded by a timeout
                        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        try
                        {
                            await host.StopAsync(shutdownCts.Token);
                        }
                        catch (OperationCanceledException) { /* timed out */ }
                        catch { /* ignore */ }
                        host.Dispose();
                    }
                }
            }, serverOption, instrumentsOption, persistOption);

            // run the command-line parser - note: handler uses the outer cts for Ctrl+C
            return await root.InvokeAsync(args ?? Array.Empty<string>());
        }
    }
}
