using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Market.Proto;
using System.Text.Json.Serialization;


namespace MarketDataServer.Sim
{
    public class BinanceLiveFeed : IMarketDataFeed
    {
        private readonly ILogger<BinanceLiveFeed> _logger;
        private readonly IHttpClientFactory _httpFactory;

        private CancellationTokenSource? _cts;
        private FeedConfig _config = new(new string[] { "BTCUSDT" });
        private readonly ConcurrentDictionary<string, ConcurrentQueue<BinanceDepthMessage>> _buffers = new();
        private readonly ConcurrentDictionary<string, long> _latestProcessedU = new();

        // events  conform to IMarketDataFeed
        public event Func<OrderBookSnapshot, Task>? SnapshotReceived;
        public event Func<OrderBookUpdate, Task>? UpdateReceived;

        // Shared deserializer options (case-insensitive property name matching)
        private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = false };

        public BinanceLiveFeed(ILogger<BinanceLiveFeed> logger, IHttpClientFactory httpFactory)
        {
            _logger = logger;
            _httpFactory = httpFactory;
        }

        public void Configure(FeedConfig config)
        {
            _config = config;
            _buffers.Clear();
            _latestProcessedU.Clear();
            foreach (var ins in config.Instruments)
            {
                // store instruments in UPPER form for consistent lookup
                var key = ins.Trim().ToUpperInvariant();
                _buffers[key] = new ConcurrentQueue<BinanceDepthMessage>();
                _latestProcessedU[key] = 0;
            }
        }

        public Task StartAsync(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(() => MainLoopWithReconnectAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            _cts?.Cancel();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            return ValueTask.CompletedTask;
        }

        // Outer loop, keep trying to re-establish websocket if it fails 
        private async Task MainLoopWithReconnectAsync(CancellationToken ct)
        {
            var backoffMs = 1000;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(ct);
                    // if RunOnceAsync returns normally (cancellation), break
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BinanceLiveFeed top-level error, will retry in {ms}ms", backoffMs);
                    try { await Task.Delay(backoffMs, ct); } catch (OperationCanceledException) { break; }
                    backoffMs = Math.Min(30000, backoffMs * 2); // cap backoff
                }
            }
        }

        // Single connection/session run
        private async Task RunOnceAsync(CancellationToken ct)
        {
            //Build combined stream URL
            var streams = string.Join("/", _config.Instruments.Select(s => $"{s.ToLowerInvariant()}@depth@100ms"));
            var url = $"wss://stream.binance.com:9443/stream?streams={streams}";

            using var client = new ClientWebSocket();
            client.Options.SetRequestHeader("User-Agent", "MarketDataServer-BinanceLiveFeed");

            _logger.LogInformation("Connecting to Binance stream: {url}", url);
            await client.ConnectAsync(new Uri(url), ct);

            _logger.LogInformation("Connected to Binance websocket.");

            //Reader task, receive combined-stream JSON messages and enqueue parsed depth messages
            var readTask = Task.Run(async () =>
            {
                var buffer = new ArraySegment<byte>(new byte[64 * 1024]);
                while (!ct.IsCancellationRequested && client.State == WebSocketState.Open)
                {
                    //assemble full message
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await client.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", ct); } catch { }
                            return;
                        }
                        ms.Write(buffer.Array!, buffer.Offset, result.Count);
                    } while (!result.EndOfMessage);

                    ms.Seek(0, SeekOrigin.Begin);
                    var msgJson = Encoding.UTF8.GetString(ms.ToArray());

                    try
                    {
                        using var doc = JsonDocument.Parse(msgJson);
                        if (doc.RootElement.TryGetProperty("stream", out var streamEl) &&
                            doc.RootElement.TryGetProperty("data", out var dataEl))
                        {
                            var stream = streamEl.GetString() ?? string.Empty;
                            var dataJson = dataEl.GetRawText();
                            var sym = stream.Split('@')[0].ToUpperInvariant();

                            //Deserialize using case-insensitive option to be robust
                            var depth = JsonSerializer.Deserialize<BinanceDepthMessage>(dataJson, _jsonOpts);
                            if (depth != null)
                            {
                                if (_buffers.TryGetValue(sym, out var q))
                                {
                                    q.Enqueue(depth);
                                }
                                else
                                {
                                    _logger.LogWarning("Received message for unexpected symbol {sym}", sym);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error parsing websocket message");
                    }
                }
            }, ct);

            //For each instrument, launch its per-instrument sync/processing loop (they run until cancelled)
            var tasks = _config.Instruments.Select(ins => Task.Run(() => PerInstrumentSyncLoop(ins.ToUpperInvariant(), ct), ct)).ToArray();

            //Wait until cancellation or any task fails
            var all = Task.WhenAll(tasks.Concat(new[] { readTask }));
            try
            {
                await all;
            }
            finally
            {
                //ensure websocket closed
                if (client.State == WebSocketState.Open)
                {
                    try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", CancellationToken.None); } catch { }
                }
            }
        }

        //Per-instrument safe sync and ongoing processing
        private async Task PerInstrumentSyncLoop(string instrument, CancellationToken ct)
        {
            // small initial delay for buffer to accumulate
            await Task.Delay(200, ct);

            //queue for this instrument
            if (!_buffers.TryGetValue(instrument, out var q))
            {
                _logger.LogWarning("PerInstrumentSyncLoop started for unknown instrument {instrument}", instrument);
                return;
            }

            var http = _httpFactory.CreateClient("binance");

            //fetch REST snapshot (BaseAddress must be set on the named client)
            var symbol = instrument.ToUpperInvariant();
            var restUrl = $"api/v3/depth?symbol={symbol}&limit=1000";
            _logger.LogInformation("Fetching REST snapshot for {symbol} from {restUrl}", symbol, restUrl);

            DepthSnapshot? snapshot = null;
            try
            {
                var resp = await http.GetAsync(restUrl, ct);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync(ct);
                snapshot = JsonSerializer.Deserialize<DepthSnapshot>(body, _jsonOpts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch REST snapshot for {symbol}", symbol);
                return;
            }

            if (snapshot == null)
            {
                _logger.LogError("Snapshot null for {symbol}", symbol);
                return;
            }

            long lastUpdateId = snapshot.LastUpdateId;
            _logger.LogInformation("REST snapshot for {symbol} lastUpdateId={id}", symbol, lastUpdateId);

            //find first buffered message that satisfies U <= lastUpdateId+1 <= u
            BinanceDepthMessage? matching = null;
            var buffered = new List<BinanceDepthMessage>();

            // Keep trying to accumulate buffered messages until the 'matching' message that bridges snapshot is found
            while (!ct.IsCancellationRequested)
            {
                while (q.TryDequeue(out var item))
                    buffered.Add(item);

                matching = buffered.Find(m => m.U <= lastUpdateId + 1 && m.u >= lastUpdateId + 1);
                if (matching != null) break;

                await Task.Delay(100, ct);
            }

            if (matching == null)
            {
                _logger.LogWarning("No matching websocket update bridging snapshot for {symbol}; performing best-effort continue", symbol);

                var snapProto = SnapshotFromDepthSnapshot(snapshot);
                if (SnapshotReceived != null) await SnapshotReceived.Invoke(snapProto);

                foreach (var msg in buffered)
                {
                    var updateProto = UpdateFromDepthMessage(msg);
                    if (UpdateReceived != null) await UpdateReceived.Invoke(updateProto);
                }
            }
            else
            {
                var idx = buffered.IndexOf(matching);
                var toApply = buffered.Skip(idx).ToList();

                var snapProto = SnapshotFromDepthSnapshot(snapshot);
                if (SnapshotReceived != null) await SnapshotReceived.Invoke(snapProto);

                foreach (var msg in toApply)
                {
                    var updateProto = UpdateFromDepthMessage(msg);
                    if (UpdateReceived != null) await UpdateReceived.Invoke(updateProto);
                    _latestProcessedU[instrument] = msg.u;
                }
            }

            // Continuous processing of new buffered messages
            while (!ct.IsCancellationRequested)
            {
                while (q.TryDequeue(out var msg))
                {
                    if (msg.u <= _latestProcessedU.GetValueOrDefault(instrument, 0)) continue;

                    var updateProto = UpdateFromDepthMessage(msg);
                    if (UpdateReceived != null) await UpdateReceived.Invoke(updateProto);
                    _latestProcessedU[instrument] = msg.u;
                }

                await Task.Delay(_config.UpdateIntervalMs, ct);
            }
        }

        // Convert REST snapshot JSON to OrderBookSnapshot proto
        private static OrderBookSnapshot SnapshotFromDepthSnapshot(DepthSnapshot ds)
        {
            var snap = new OrderBookSnapshot { InstrumentId = ds.Symbol, Sequence = ds.LastUpdateId };
            foreach (var b in ds.Bids)
            {
                var p = double.Parse(b[0], CultureInfo.InvariantCulture);
                var q = double.Parse(b[1], CultureInfo.InvariantCulture);
                snap.Bids.Add(new PriceLevel { Price = p, Quantity = q, Level = 0 });
            }
            foreach (var a in ds.Asks)
            {
                var p = double.Parse(a[0], CultureInfo.InvariantCulture);
                var q = double.Parse(a[1], CultureInfo.InvariantCulture);
                snap.Asks.Add(new PriceLevel { Price = p, Quantity = q, Level = 0 });
            }
            return snap;
        }

        // Convert Binance depth update message to OrderBookUpdate proto (simple mapping)
        private static OrderBookUpdate UpdateFromDepthMessage(BinanceDepthMessage msg)
        {
            var u = new OrderBookUpdate { InstrumentId = msg.s, Sequence = msg.u }; // use final update id 'u' as sequence
            foreach (var b in msg.b)
            {
                var price = double.Parse(b[0], CultureInfo.InvariantCulture);
                var qty = double.Parse(b[1], CultureInfo.InvariantCulture);
                var change = new PriceLevelChange
                {
                    Level = new PriceLevel { Price = price, Quantity = qty, Level = 0 },
                    Type = qty == 0 ? PriceLevelChange.Types.ChangeType.Remove : PriceLevelChange.Types.ChangeType.Update
                };
                u.Changes.Add(change);
            }
            foreach (var a in msg.a)
            {
                var price = double.Parse(a[0], CultureInfo.InvariantCulture);
                var qty = double.Parse(a[1], CultureInfo.InvariantCulture);
                var change = new PriceLevelChange
                {
                    Level = new PriceLevel { Price = price, Quantity = qty, Level = 0 },
                    Type = qty == 0 ? PriceLevelChange.Types.ChangeType.Remove : PriceLevelChange.Types.ChangeType.Update
                };
                u.Changes.Add(change);
            }
            return u;
        }

        // DTOs for Binance JSON
        private class BinanceDepthMessage
        {
            [JsonPropertyName("e")]
            public string EventType { get; set; } = default!; // maps to JSON 'e'

            [JsonPropertyName("E")]
            public long EventTime { get; set; }                   // maps to JSON 'E'

            [JsonPropertyName("s")]
            public string s { get; set; } = default!;             // symbol (s)

            [JsonPropertyName("U")]
            public long U { get; set; }                           // first update ID in event

            [JsonPropertyName("u")]
            public long u { get; set; }                           // final update ID in event

            [JsonPropertyName("b")]
            public List<List<string>> b { get; set; } = new();    // bids [ [price, qty], ... ]

            [JsonPropertyName("a")]
            public List<List<string>> a { get; set; } = new();    // asks
        }
        private class DepthSnapshot
        {
            [JsonPropertyName("lastUpdateId")]
            public long LastUpdateId { get; set; }

            [JsonPropertyName("symbol")]
            public string Symbol { get; set; } = string.Empty;

            [JsonPropertyName("bids")]
            public List<List<string>> Bids { get; set; } = new();

            [JsonPropertyName("asks")]
            public List<List<string>> Asks { get; set; } = new();
        }
    }
}
