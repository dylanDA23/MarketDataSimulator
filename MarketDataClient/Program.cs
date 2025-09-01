using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Threading.Tasks;
using Spectre.Console;

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
                // Return success quickly; dotnet-ef will proceed to use the design-time factory.
                return 0;
            }

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
                description: "Persist incoming messages to local DB (NOT enabled in this lightweight build).",
                getDefaultValue: () => false);

            root.AddOption(serverOption);
            root.AddOption(instrumentsOption);
            root.AddOption(persistOption);

            // Defensive handler: catches and reports exceptions from the client run.
            root.SetHandler(async (string server, string[] instruments, bool persist) =>
            {
                try
                {
                    // Normalize / validate server URL
                    var serverUrl = string.IsNullOrWhiteSpace(server)
                        ? Environment.GetEnvironmentVariable("MARKETDATA_SERVER_URL") ?? "http://localhost:5000"
                        : server;

                    // Normalize instruments: split comma-separated entries and trim, uppercase
                    string[] instrumentsList;
                    if (instruments == null || instruments.Length == 0)
                    {
                        instrumentsList = new[] { "BTCUSDT" };
                    }
                    else
                    {
                        instrumentsList = instruments
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            .Select(s => s.Trim().ToUpperInvariant())
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToArray();

                        if (instrumentsList.Length == 0)
                            instrumentsList = new[] { "BTCUSDT" };
                    }

                    // Small, robust startup UI that avoids DI extension method calls.
                    AnsiConsole.Clear();
                    AnsiConsole.Write(new FigletText("MarketDataClient").Centered());

                    // Escape untrusted strings before injecting into Spectre.Markup
                    var escServer = Markup.Escape(serverUrl);
                    var escInstruments = Markup.Escape(string.Join(',', instrumentsList));

                    // Use GruvboxTheme color but escape markup inputs
                    try
                    {
                        AnsiConsole.MarkupLine($"[{GruvboxTheme.BrightAqua}]Server:[/] {escServer} [grey]| Instruments:[/] {escInstruments}");
                    }
                    catch
                    {
                        // Fallback if color/style parsing fails for any reason
                        AnsiConsole.WriteLine($"Server: {escServer} | Instruments: {escInstruments}");
                    }

                    if (persist)
                    {
                        AnsiConsole.MarkupLine("[yellow]Note:[/] persistence (--persist) is disabled in this build. To enable it you must add EF and hosting packages to the project. See the README notes.");
                    }

                    using var http = new System.Net.Http.HttpClient();
                    // Now pass only non-null, validated values (silences CS8604)
                    var client = new MarketDataConsoleClient(serverUrl, instrumentsList, http, persist);

                    // If RunAsync throws, we will catch below and print a helpful message
                    await client.RunAsync();
                }
                catch (Exception ex)
                {
                    // Display the error in the console UI clearly and rethrow (to give non-zero exit code if desired)
                    try
                    {
                        AnsiConsole.MarkupLine($"[red]Unhandled client error:[/] {Markup.Escape(ex.Message)}");
                        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything | ExceptionFormats.ShowLinks);
                    }
                    catch
                    {
                        // If markup/rendering fails, fall back to Console.WriteLine
                        Console.WriteLine("Unhandled client error: " + ex.ToString());
                    }

                    // Re-throw so the process exits non-zero and you can see the stack / CI can detect failure
                    throw;
                }
            }, serverOption, instrumentsOption, persistOption);

            // Pass a non-null args array to avoid CS8604 analyzer warning.
            return await root.InvokeAsync(args ?? Array.Empty<string>());
        }
    }
}
