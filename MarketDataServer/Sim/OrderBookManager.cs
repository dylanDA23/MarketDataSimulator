using System;
using System.Collections.Concurrent;
using System.Linq;
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

        // store the latest snapshot for each instrument 
        private readonly ConcurrentDictionary<string, OrderBookSnapshot> _latestSnapshots
            = new(StringComparer.OrdinalIgnoreCase);

        public OrderBookManager(IMarketDataFeed feed, ILogger<OrderBookManager> logger)
        {
            _feed = feed ?? throw new ArgumentNullException(nameof(feed));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // subscribe to feed events
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

            if (!_clients.TryGetValue(clientId, out var clientEntry)) return;

            clientEntry.subs[instrumentId] = 1;
            _logger.LogDebug("Client {ClientId} subscribed to {Instrument}", clientId, instrumentId);

            // Immediately send latest snapshotto the newly subscribed client
            if (_latestSnapshots.TryGetValue(instrumentId, out var latest))
            {
                try
                {
                    clientEntry.writer.TryWrite(new MarketDataMessage { Snapshot = latest });
                    _logger.LogInformation("Sent latest snapshot to client {ClientId} for {Instrument} (seq={Seq})",
                        clientId, instrumentId, latest.Sequence);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send latest snapshot to client {ClientId} for {Instrument}", clientId, instrumentId);
                }
            }
            else
            {
                _logger.LogDebug("No stored snapshot available yet for {Instrument} when client {ClientId} subscribed", instrumentId, clientId);
            }
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
            var key = snapshot.InstrumentId?.Trim().ToUpperInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key)) return Task.CompletedTask;

            // store latest snapshot 
            _latestSnapshots[key] = snapshot;

            // log receipt (important debug line)
            _logger.LogInformation("[SNAPSHOT] Received snapshot for {Instrument} seq={Seq}", key, snapshot.Sequence);

            // count how many currently subscribed clients would receive it
            var subscriberCount = _clients.Values.Count(e => e.subs.ContainsKey(key));
            _logger.LogInformation("[SNAPSHOT] Broadcasting snapshot for {Instrument} to {Count} subscribers", key, subscriberCount);

            // broadcast to subscribed clients
            Broadcast(key, new MarketDataMessage { Snapshot = snapshot });
            return Task.CompletedTask;
        }

        private Task OnUpdate(OrderBookUpdate update)
        {
            if (update == null) return Task.CompletedTask;
            var key = update.InstrumentId?.Trim().ToUpperInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key)) return Task.CompletedTask;

            // log update receipt for visibility
            _logger.LogDebug("[UPDATE] Received update for {Instrument} seq={Seq}", key, update.Sequence);

            // Updates do not change the stored snapshot here 
            Broadcast(key, new MarketDataMessage { Update = update });
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
                        var wrote = kv.Value.writer.TryWrite(msg);
                        if (!wrote)
                        {
                            _logger.LogWarning("TryWrite returned false for client {ClientId} instrument {Instrument}", kv.Key, instrumentIdUpper);
                        }
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
