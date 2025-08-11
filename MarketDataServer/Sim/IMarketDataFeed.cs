using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Market.Proto;

namespace MarketDataServer.Sim
{
    public interface IMarketDataFeed : IAsyncDisposable
    {
        void Configure(FeedConfig config);
        Task StartAsync(CancellationToken ct);
        Task StopAsync(CancellationToken ct);
        event Func<OrderBookSnapshot, Task>? SnapshotReceived;
        event Func<OrderBookUpdate, Task>? UpdateReceived;
    }

    public record FeedConfig(string[] Instruments, int InitialDepth = 5, int UpdateIntervalMs = 200, int SnapshotIntervalSec = 5);
}
