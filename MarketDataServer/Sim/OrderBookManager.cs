using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Market.Proto;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MarketDataServer.Sim
{
    public class OrderBookManager
    {
        private readonly IMarketDataFeed _feed;
        private readonly ILogger<OrderBookManager> _logger;

        // clientId -> (writer, subscriptions)
        private readonly ConcurrentDictionary<string, (ChannelWriter<MarketDataMessage> writer, ConcurrentDictionary<string, byte> subs)> _clients
            = new();

        public OrderBookManager(IMarketDataFeed feed, ILogger<OrderBookManager> logger)
        {
            _feed = feed;
            _logger = logger;
            _feed.SnapshotReceived += OnSnapshot;
            _feed.UpdateReceived += OnUpdate;
        }

        public void RegisterClient(string clientId, ChannelWriter<MarketDataMessage> writer)
        {
            _clients[clientId] = (writer, new ConcurrentDictionary<string, byte>());
        }

        public void UnregisterClient(string clientId)
        {
            if (_clients.TryRemove(clientId, out var t))
            {
                try { t.writer.TryComplete(); } catch { }
            }
        }

        public void SubscribeClientToInstrument(string clientId, string instrumentId)
        {
            if (!_clients.ContainsKey(clientId)) return;
            _clients[clientId].subs[instrumentId] = 1;
        }

        public void UnsubscribeClient(string clientId, string instrumentId)
        {
            if (_clients.TryGetValue(clientId, out var entry))
            {
                entry.subs.TryRemove(instrumentId, out _);
                var empty = new EmptySnapshot { InstrumentId = instrumentId };
                entry.writer.TryWrite(new MarketDataMessage { EmptySnapshot = empty });
            }
        }

        private Task OnSnapshot(OrderBookSnapshot snapshot)
        {
            Broadcast(snapshot.InstrumentId, new MarketDataMessage { Snapshot = snapshot });
            return Task.CompletedTask;
        }

        private Task OnUpdate(OrderBookUpdate update)
        {
            Broadcast(update.InstrumentId, new MarketDataMessage { Update = update });
            return Task.CompletedTask;
        }

        private void Broadcast(string instrumentId, MarketDataMessage msg)
        {
            foreach (var kv in _clients)
            {
                if (kv.Value.subs.ContainsKey(instrumentId))
                {
                    try { kv.Value.writer.TryWrite(msg); } catch { }
                }
            }
        }

        public Task StartAsync(CancellationToken ct)
        {
            var ins = new[] { "BTCUSDT", "ETHUSDT" };
            _feed.Configure(new FeedConfig(ins, InitialDepth: 5, UpdateIntervalMs: 200, SnapshotIntervalSec: 5));
            return _feed.StartAsync(ct);
        }
    }
}
