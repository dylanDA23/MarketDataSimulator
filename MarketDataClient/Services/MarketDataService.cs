using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Market.Proto;
using MarketDataClient.UI;

namespace MarketDataClient.Services
{
    /// <summary>
    /// A simple in-memory hub for orderbook state that both the PersisterWorker
    /// and the console UI can use. When persistence is enabled the PersisterWorker
    /// is the single gRPC consumer and it forwards snapshots/updates here; the UI
    /// reads from this service instead of opening a second gRPC connection.
    /// </summary>
    public class MarketDataService
    {
        private readonly ConcurrentDictionary<string, OrderBookModel> _books
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Optional initial instruments to pre-create orderbooks for.
        /// </summary>
        public MarketDataService(IEnumerable<string>? initialInstruments = null)
        {
            if (initialInstruments != null)
            {
                foreach (var ins in initialInstruments)
                {
                    if (string.IsNullOrWhiteSpace(ins)) continue;
                    _books.TryAdd(ins.ToUpperInvariant(), new OrderBookModel(ins));
                }
            }
        }

        public OrderBookModel GetOrCreate(string instrumentId)
        {
            if (string.IsNullOrWhiteSpace(instrumentId))
                throw new ArgumentNullException(nameof(instrumentId));

            var key = instrumentId.ToUpperInvariant();
            return _books.GetOrAdd(key, k => new OrderBookModel(k));
        }

        public void ApplySnapshot(OrderBookSnapshot snap)
        {
            if (snap == null) return;
            GetOrCreate(snap.InstrumentId).ApplySnapshot(snap);
        }

        public void ApplyUpdate(OrderBookUpdate upd)
        {
            if (upd == null) return;
            GetOrCreate(upd.InstrumentId).ApplyUpdate(upd);
        }

        /// <summary>
        /// Read-only view for UI rendering loops.
        /// </summary>
        public IReadOnlyDictionary<string, OrderBookModel> GetAll() => _books;
    }
}
