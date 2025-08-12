using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Market.Proto;
using System.Collections.Generic;
using System.Threading;

namespace MarketDataServer.Sim
{
    public class OrderBook
    {
        public string InstrumentId { get; }
        private readonly List<(double price, double qty, int level)> _bids = new();
        private readonly List<(double price, double qty, int level)> _asks = new();
        private long _sequence = 0;

        public OrderBook(string id) => InstrumentId = id;

        public static OrderBook CreateInitial(string id, int depth)
        {
            var ob = new OrderBook(id);
            var basePrice = 100.0 + Math.Abs(id.GetHashCode() % 100);
            for (int i = 0; i < depth; i++)
            {
                ob._bids.Add((basePrice - i * 0.5, 10 + i, i));
                ob._asks.Add((basePrice + (i + 1) * 0.5, 10 + i, i));
            }
            return ob;
        }

        public OrderBookSnapshot ToSnapshotMessage()
        {
            var snap = new OrderBookSnapshot { InstrumentId = InstrumentId, Sequence = Interlocked.Increment(ref _sequence) };
            foreach (var b in _bids) snap.Bids.Add(new PriceLevel { Price = b.price, Quantity = b.qty, Level = b.level });
            foreach (var a in _asks) snap.Asks.Add(new PriceLevel { Price = a.price, Quantity = a.qty, Level = a.level });
            return snap;
        }

        public OrderBookUpdate? RandomUpdate(Random rng)
        {
            var roll = rng.NextDouble();
            var isBid = rng.NextDouble() > 0.5;
            var list = isBid ? _bids : _asks;
            if (list.Count == 0 || roll < 0.4)
            {
                var level = list.Count;
                var price = (isBid ? (list.FirstOrDefault().price) - 0.5 : (list.FirstOrDefault().price) + 0.5);
                var qty = Math.Round(1 + rng.NextDouble() * 20, 2);
                list.Insert(level, (price, qty, level));
                var u = new OrderBookUpdate { InstrumentId = InstrumentId, Sequence = Interlocked.Increment(ref _sequence) };
                u.Changes.Add(new PriceLevelChange { Type = PriceLevelChange.Types.ChangeType.Add, Level = new PriceLevel { Price = price, Quantity = qty, Level = level } });
                return u;
            }
            else if (roll < 0.8)
            {
                var idx = rng.Next(0, list.Count);
                var p = list[idx];
                var newQty = Math.Round(Math.Max(0, p.qty + (rng.NextDouble() - 0.5) * 5), 2);
                list[idx] = (p.price, newQty, p.level);
                var u = new OrderBookUpdate { InstrumentId = InstrumentId, Sequence = Interlocked.Increment(ref _sequence) };
                u.Changes.Add(new PriceLevelChange { Type = PriceLevelChange.Types.ChangeType.Update, Level = new PriceLevel { Price = p.price, Quantity = newQty, Level = p.level } });
                return u;
            }
            else
            {
                if (list.Count == 0) return null;
                var idx = rng.Next(0, list.Count);
                var p = list[idx];
                list.RemoveAt(idx);
                var u = new OrderBookUpdate { InstrumentId = InstrumentId, Sequence = Interlocked.Increment(ref _sequence) };
                u.Changes.Add(new PriceLevelChange { Type = PriceLevelChange.Types.ChangeType.Remove, Level = new PriceLevel { Price = p.price, Quantity = p.qty, Level = p.level } });
                return u;
            }
        }
    }
}
