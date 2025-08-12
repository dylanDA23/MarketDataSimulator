using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Market.Proto;
using MarketDataServer.Sim;
using System.Threading.Channels;

namespace MarketDataServer.Services
{
    public class MarketDataService : MarketData.MarketDataBase
    {
        private readonly OrderBookManager _manager;

        public MarketDataService(OrderBookManager manager) => _manager = manager;

        public override async Task Subscribe(IAsyncStreamReader<SubscriptionRequest> requestStream,
                                             IServerStreamWriter<MarketDataMessage> responseStream,
                                             ServerCallContext context)
        {
            var clientId = Guid.NewGuid().ToString();
            var channel = Channel.CreateUnbounded<MarketDataMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

            _manager.RegisterClient(clientId, channel.Writer);

            var readTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var req in requestStream.ReadAllAsync(context.CancellationToken))
                    {
                        if (req.Unsubscribe)
                            _manager.UnsubscribeClient(clientId, req.InstrumentId);
                        else
                            _manager.SubscribeClientToInstrument(clientId, req.InstrumentId);
                    }
                }
                catch (OperationCanceledException) { }
            }, context.CancellationToken);

            try
            {
                while (await channel.Reader.WaitToReadAsync(context.CancellationToken))
                {
                    while (channel.Reader.TryRead(out var msg))
                    {
                        await responseStream.WriteAsync(msg);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _manager.UnregisterClient(clientId);
                channel.Writer.TryComplete();
            }

            await readTask;
        }
    }
}
