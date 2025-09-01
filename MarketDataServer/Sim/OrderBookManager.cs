using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Market.Proto;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

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
            _feed = feed ?? throw new ArgumentNullException(nameof(feed));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _feed.SnapshotReceived += OnSnapshot;
            _feed.UpdateReceived += OnUpdate;
        }

        public void RegisterClient(string clientId, ChannelWriter<MarketDataMessage> writer)
        {
            if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentNullException(nameof(clientId));
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            _clients[clientId] = (writer, new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            _logger.LogDebug("Registered client {ClientId}", clientId);
        }

        public void UnregisterClient(string clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId)) return;
            if (_clients.TryRemove(clientId, out var t))
            {
                try { t.writer.TryComplete(); } catch { }
                _logger.LogDebug("Unregistered client {ClientId}", clientId);
            }
        }

        public void SubscribeClientToInstrument(string clientId, string instrumentId)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(instrumentId)) return;
            instrumentId = instrumentId.Trim().ToUpperInvariant();
            if (!_clients.ContainsKey(clientId)) return;
            _clients[clientId].subs[instrumentId] = 1;
            _logger.LogDebug("Client {ClientId} subscribed to {Instrument}", clientId, instrumentId);
        }

        public void UnsubscribeClient(string clientId, string instrumentId)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(instrumentId)) return;
            instrumentId = instrumentId.Trim().ToUpperInvariant();

            if (_clients.TryGetValue(clientId, out var entry))
            {
                entry.subs.TryRemove(instrumentId, out _);
                var empty = new EmptySnapshot { InstrumentId = instrumentId };
                try { entry.writer.TryWrite(new MarketDataMessage { EmptySnapshot = empty }); } catch { }
                _logger.LogDebug("Client {ClientId} unsubscribed from {Instrument}", clientId, instrumentId);
            }
        }

        private Task OnSnapshot(OrderBookSnapshot snapshot)
        {
            if (snapshot == null) return Task.CompletedTask;
            Broadcast(snapshot.InstrumentId?.Trim().ToUpperInvariant() ?? string.Empty, new MarketDataMessage { Snapshot = snapshot });
            return Task.CompletedTask;
        }

        private Task OnUpdate(OrderBookUpdate update)
        {
            if (update == null) return Task.CompletedTask;
            Broadcast(update.InstrumentId?.Trim().ToUpperInvariant() ?? string.Empty, new MarketDataMessage { Update = update });
            return Task.CompletedTask;
        }

        private void Broadcast(string instrumentIdUpper, MarketDataMessage msg)
        {
            if (string.IsNullOrWhiteSpace(instrumentIdUpper)) return;

            foreach (var kv in _clients)
            {
                try
                {
                    if (kv.Value.subs.ContainsKey(instrumentIdUpper))
                    {
                        kv.Value.writer.TryWrite(msg);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to broadcast to client {ClientId}", kv.Key);
                }
            }
        }

        public Task StartAsync(CancellationToken ct)
        {
            var ins = new[] { "BTCUSDT", "ETHUSDT" };
            _feed.Configure(new FeedConfig(ins, InitialDepth: 10, UpdateIntervalMs: 200, SnapshotIntervalSec: 5));
            _logger.LogInformation("OrderBookManager starting feed (instruments: {Count})", ins.Length);
            return _feed.StartAsync(ct);
        }
    }
}
