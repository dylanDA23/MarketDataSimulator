// MarketDataClient/UI/ConsoleModels.cs
using System;
using System.Collections.Generic;
using System.Linq;
// MarketDataClient/UI/ConsoleModels.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Market.Proto;

namespace MarketDataClient.UI
{
    public class PriceLevelModel
    {
        public double Price { get; set; }
        public double Quantity { get; set; }
        public int Level { get; set; }
    }

    public class OrderBookSnapshotModel
    {
        public string InstrumentId { get; init; } = string.Empty;
        public long LastSequence { get; init; }
        public List<PriceLevelModel> Bids { get; init; } = new();
        public List<PriceLevelModel> Asks { get; init; } = new();
    }

    public class OrderBookModel
    {
        public string InstrumentId { get; }
        private readonly object _lock = new();
        private readonly List<PriceLevelModel> _bids = new();
        private readonly List<PriceLevelModel> _asks = new();
        private long _lastSequence;

        public OrderBookModel(string instrumentId)
        {
            InstrumentId = (instrumentId ?? throw new ArgumentNullException(nameof(instrumentId))).ToUpperInvariant();
        }

        public void ApplySnapshot(OrderBookSnapshot snap)
        {
            if (snap == null) return;
            lock (_lock)
            {
                _lastSequence = snap.Sequence;
                _bids.Clear();
                _asks.Clear();
                foreach (var b in snap.Bids)
                    _bids.Add(new PriceLevelModel { Price = b.Price, Quantity = b.Quantity, Level = b.Level });
                foreach (var a in snap.Asks)
                    _asks.Add(new PriceLevelModel { Price = a.Price, Quantity = a.Quantity, Level = a.Level });

                _bids.Sort((x, y) => y.Price.CompareTo(x.Price));
                _asks.Sort((x, y) => x.Price.CompareTo(y.Price));
            }
        }

        public void ApplyUpdate(OrderBookUpdate upd)
        {
            if (upd == null) return;
            lock (_lock)
            {
                _lastSequence = upd.Sequence;
                foreach (var ch in upd.Changes)
                {
                    var p = ch.Level.Price;
                    var q = ch.Level.Quantity;

                    if (ch.Type == PriceLevelChange.Types.ChangeType.Remove || Math.Abs(q) < 1e-12)
                    {
                        _bids.RemoveAll(x => Math.Abs(x.Price - p) < 1e-12);
                        _asks.RemoveAll(x => Math.Abs(x.Price - p) < 1e-12);
                        continue;
                    }

                    var idxB = _bids.FindIndex(x => Math.Abs(x.Price - p) < 1e-12);
                    if (idxB >= 0)
                    {
                        _bids[idxB].Quantity = q;
                        continue;
                    }

                    var idxA = _asks.FindIndex(x => Math.Abs(x.Price - p) < 1e-12);
                    if (idxA >= 0)
                    {
                        _asks[idxA].Quantity = q;
                        continue;
                    }

                    double mid = 0;
                    if (_bids.Count > 0 && _asks.Count > 0) mid = (_bids[0].Price + _asks[0].Price) / 2.0;
                    else if (_bids.Count > 0) mid = _bids[0].Price;
                    else if (_asks.Count > 0) mid = _asks[0].Price;

                    if (p <= mid) _bids.Add(new PriceLevelModel { Price = p, Quantity = q, Level = ch.Level.Level });
                    else _asks.Add(new PriceLevelModel { Price = p, Quantity = q, Level = ch.Level.Level });
                }

                _bids.Sort((a, b) => b.Price.CompareTo(a.Price));
                _asks.Sort((a, b) => a.Price.CompareTo(b.Price));
            }
        }

        public OrderBookSnapshotModel GetSnapshot()
        {
            lock (_lock)
            {
                return new OrderBookSnapshotModel
                {
                    InstrumentId = InstrumentId,
                    LastSequence = _lastSequence,
                    Bids = _bids.Select(x => new PriceLevelModel { Price = x.Price, Quantity = x.Quantity, Level = x.Level }).ToList(),
                    Asks = _asks.Select(x => new PriceLevelModel { Price = x.Price, Quantity = x.Quantity, Level = x.Level }).ToList()
                };
            }
        }
    }
}
