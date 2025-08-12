using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Market.Proto;
using System.Collections.Concurrent;

namespace MarketDataServer.Sim
{
    public class SimulationFeed : IMarketDataFeed
    {
        private readonly ILogger<SimulationFeed> _logger;
        private CancellationTokenSource? _cts;
        private FeedConfig _config = new(new string[] { "BTCUSDT" });
        private readonly ConcurrentDictionary<string, OrderBook> _books = new();

        public event Func<OrderBookSnapshot, Task>? SnapshotReceived;
        public event Func<OrderBookUpdate, Task>? UpdateReceived;

        public SimulationFeed(ILogger<SimulationFeed> logger) => _logger = logger;

        public void Configure(FeedConfig config)
        {
            _config = config;
            foreach (var ins in config.Instruments)
                _books[ins] = OrderBook.CreateInitial(ins, config.InitialDepth);
        }

        public Task StartAsync(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(() => Loop(_cts.Token));
            return Task.CompletedTask;
        }

        private async Task Loop(CancellationToken ct)
        {
            var rng = new Random();
            while (!ct.IsCancellationRequested)
            {
                foreach (var kv in _books)
                {
                    var ob = kv.Value;
                    var update = ob.RandomUpdate(rng);
                    if (update != null && UpdateReceived != null) await UpdateReceived.Invoke(update);

                    if (DateTime.UtcNow.Second % _config.SnapshotIntervalSec == 0)
                    {
                        var snap = ob.ToSnapshotMessage();
                        if (SnapshotReceived != null) await SnapshotReceived.Invoke(snap);
                    }
                }
                await Task.Delay(_config.UpdateIntervalMs, ct);
            }
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
    }
}
