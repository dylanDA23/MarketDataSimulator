// ~/Desktop/MarketDataSimulator/MarketDataClient/MarketDataConsoleClient.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Market.Proto;
using Spectre.Console;
using MarketDataClient.UI; // explicit UI models

namespace MarketDataClient
{
    public class MarketDataConsoleClient : IAsyncDisposable
    {
        private readonly string _serverUrl;
        private readonly string[] _instruments;
        private readonly HttpClient _http;
        private readonly bool _persist;

        private readonly ConcurrentDictionary<string, MarketDataClient.UI.OrderBookModel> _books = new();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public MarketDataConsoleClient(string serverUrl, IEnumerable<string> instruments, HttpClient httpClient, bool persist = false)
        {
            _serverUrl = serverUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(serverUrl));
            _instruments = instruments.Select(i => i.Trim().ToUpperInvariant()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            _http = httpClient ?? new HttpClient();
            _persist = persist;

            foreach (var ins in _instruments)
                _books[ins] = new MarketDataClient.UI.OrderBookModel(ins);
        }

        public async Task RunAsync()
        {
            // allow plaintext http2 for local dev
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            using var channel = GrpcChannel.ForAddress(_serverUrl, new GrpcChannelOptions { HttpClient = _http });
            var client = new MarketData.MarketDataClient(channel);
            using var call = client.Subscribe();

            // initial subscriptions
            foreach (var ins in _instruments)
            {
                await call.RequestStream.WriteAsync(new SubscriptionRequest { InstrumentId = ins, Unsubscribe = false });
            }

            // reader loop
            var readTask = Task.Run(async () =>
            {
                try
                {
                    while (await call.ResponseStream.MoveNext(_cts.Token))
                    {
                        var msg = call.ResponseStream.Current;
                        try
                        {
                            if (msg.PayloadCase == MarketDataMessage.PayloadOneofCase.Snapshot)
                            {
                                var snap = msg.Snapshot;
                                if (_books.TryGetValue(snap.InstrumentId.ToUpperInvariant(), out var book))
                                    book.ApplySnapshot(snap);
                            }
                            else if (msg.PayloadCase == MarketDataMessage.PayloadOneofCase.Update)
                            {
                                var update = msg.Update;
                                if (_books.TryGetValue(update.InstrumentId.ToUpperInvariant(), out var book))
                                    book.ApplyUpdate(update);
                            }
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]Error processing incoming message:[/] {Markup.Escape(ex.Message)}");
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, _cts.Token);

            await RenderLoopAsync(_cts.Token);

            // graceful shutdown
            try { await call.RequestStream.CompleteAsync(); } catch { }
            _cts.Cancel();
            await readTask;
        }

        private async Task RenderLoopAsync(CancellationToken ct)
        {
            var refreshDelay = TimeSpan.FromMilliseconds(150);

            // Figlet header (uncolored)
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("MarketDataClient").Centered());

            // A helpful header line using hex color codes safely
            var headerColor = SafeColorTag(GruvboxTheme.BrightAqua);
            AnsiConsole.MarkupLine($"[{headerColor}]Connected to CLI mode - Gruvbox theme active[/]");

            // --- last sequence tracker to avoid redraws when nothing changed ---
            var lastSeq = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var ins in _instruments)
                lastSeq[ins] = 0;

            while (!ct.IsCancellationRequested)
            {
                // decide whether any instrument changed since last render
                bool anyChanged = false;
                foreach (var ins in _instruments)
                {
                    if (!_books.TryGetValue(ins, out var book)) continue;
                    var snap = book.GetSnapshot();
                    if (snap != null && snap.LastSequence != lastSeq[ins])
                    {
                        lastSeq[ins] = snap.LastSequence;
                        anyChanged = true;
                    }
                }

                if (!anyChanged)
                {
                    try
                    {
                        await Task.Delay(refreshDelay, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"[grey]Connected to[/] [bold]{_serverUrl}[/] [grey]| Instruments:[/] [bold]{string.Join(',', _instruments)}[/]");
                sb.AppendLine();

                foreach (var ins in _instruments)
                {
                    if (!_books.TryGetValue(ins, out var book)) continue;

                    var snap = book.GetSnapshot();

                    var blue = SafeColorTag(GruvboxTheme.BrightBlue);
                    var yellow = SafeColorTag(GruvboxTheme.GoldenYellow);

                    sb.AppendLine($"[{blue}]{EscapeMarkup(snap.InstrumentId)}[/] [grey](seq {snap.LastSequence})[/]");
                    sb.AppendLine($"[{yellow}]BIDS[/]    [{yellow}]ASKS[/]");
                    int depthToShow = 10;

                    for (int i = 0; i < depthToShow; i++)
                    {
                        var left = i < snap.Bids.Count ? FormatLevel(snap.Bids[i]) : "[grey]-[/]";
                        var right = i < snap.Asks.Count ? FormatLevel(snap.Asks[i]) : "[grey]-[/]";
                        sb.AppendLine($"{left,-40} {right}");
                    }

                    sb.AppendLine();
                }

                AnsiConsole.Clear();
                AnsiConsole.Markup(sb.ToString());

                try
                {
                    await Task.Delay(refreshDelay, ct);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        private string FormatLevel(MarketDataClient.UI.PriceLevelModel lvl)
        {
            var qty = lvl.Quantity;
            var color = qty > 50 ? GruvboxTheme.Green : GruvboxTheme.Foreground;
            var colorTag = SafeColorTag(color);
            return $"[{colorTag}]{lvl.Price:F2} @ {lvl.Quantity:F4}[/]";
        }

        private static string SafeColorTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return "default";
            return tag;
        }

        private static string EscapeMarkup(string text)
        {
            return Markup.Escape(text ?? string.Empty);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _cts.Dispose();
            await Task.CompletedTask;
        }
    }
}
