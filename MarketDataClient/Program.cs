using System.CommandLine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using MarketDataClient.Data;
using MarketDataClient.Workers;

namespace MarketDataClient
{
    internal static class Program
    {
        static async Task<int> Main(string[] args)
        {
           
            if (args != null && args.Length > 0 &&
                (args.Any(a => a.StartsWith("--applicationName", StringComparison.OrdinalIgnoreCase))
                 || args.Any(a => a.StartsWith("--depsfile", StringComparison.OrdinalIgnoreCase))
                 || args.Any(a => a.StartsWith("--additionalProbingPath", StringComparison.OrdinalIgnoreCase))
                 || args.Any(a => a.StartsWith("--fx-version", StringComparison.OrdinalIgnoreCase))
                 || args.Any(a => a.StartsWith("--runtimeconfig", StringComparison.OrdinalIgnoreCase))
                 || args.Any(a => a.StartsWith("--additionalProbingPaths", StringComparison.OrdinalIgnoreCase))
                 || args.Any(a => a.StartsWith("--roll-forward", StringComparison.OrdinalIgnoreCase))))
            {
                //Return success quickly.
                return 0;
            }

            //CLI options 
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

            //Handler
            root.SetHandler(async (string server, string[] instruments, bool persist) =>
            {
                //Normalize inputs
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
                        //read connection string from environment (the design-time factory also uses the same env var)
                        var conn = Environment.GetEnvironmentVariable("CLIENT_POSTGRES_CONN")
                                   ?? Environment.GetEnvironmentVariable("SERVER_POSTGRES_CONN")
                                   ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=marketdb";

                        host = Host.CreateDefaultBuilder()
                            .ConfigureServices((ctx, services) =>
                            {
                                //Register DbContext and PersisterWorker as a hosted service
                                services.AddDbContext<ClientPersistenceDbContext>(options =>
                                    options.UseNpgsql(conn, b => b.EnableRetryOnFailure()));

                                services.AddHostedService<PersisterWorker>();

                                //Give PersisterWorker access to IHttpClientFactory if it needs one
                                services.AddHttpClient();
                            })
                            .Build();

                        //Start the host 
                        await host.StartAsync();
                        AnsiConsole.MarkupLine("[green]Persistence enabled:[/] Persister worker started.");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[yellow]Note:[/] persistence (--persist) not requested. Running UI only.");
                    }

                    //Run the console UI client which subscribes to the gRPC server
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

                    using var http = new HttpClient();
                    var client = new MarketDataConsoleClient(serverUrl, instrumentsList, http, persist);
                    await client.RunAsync();
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
                        //shutdown the host cleanly when console UI exits
                        await host.StopAsync();
                        host.Dispose();
                    }
                }
            }, serverOption, instrumentsOption, persistOption);

            return await root.InvokeAsync(args ?? Array.Empty<string>());
        }
    }
}
