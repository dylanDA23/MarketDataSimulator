using System;
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

            // Register the client with the manager (manager will broadcast to the channel writer)
            _manager.RegisterClient(clientId, channel.Writer);

            // Reader: handle incoming subscription/unsubscribe requests from the client
            var readTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var req in requestStream.ReadAllAsync(context.CancellationToken))
                    {
                        // Debug log: record the subscription request for visibility during debugging
                        Console.WriteLine($"[DEBUG] Received subscribe request: Instrument={req.InstrumentId}, Unsubscribe={req.Unsubscribe}");

                        if (req.Unsubscribe)
                        {
                            _manager.UnsubscribeClient(clientId, req.InstrumentId);
                        }
                        else
                        {
                            _manager.SubscribeClientToInstrument(clientId, req.InstrumentId);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DEBUG] Error reading subscription stream: {ex}");
                }
            }, context.CancellationToken);

            try
            {
                // Main send loop: forward whatever is written to the per-client channel to the gRPC response stream
                while (await channel.Reader.WaitToReadAsync(context.CancellationToken))
                {
                    while (channel.Reader.TryRead(out var msg))
                    {
                        try
                        {
                            await responseStream.WriteAsync(msg);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[DEBUG] Error writing to response stream for client {clientId}: {ex}");
                        }
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
