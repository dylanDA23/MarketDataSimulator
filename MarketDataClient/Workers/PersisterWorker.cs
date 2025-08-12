using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Market.Proto;
using MarketDataClient.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MarketDataClient.Workers
{
    public class PersisterWorker : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<PersisterWorker> _logger;

        public PersisterWorker(IServiceProvider sp, ILogger<PersisterWorker> logger)
        {
            _sp = sp;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // ensure client DB migrated
            using (var scope = _sp.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ClientPersistenceDbContext>();
                await db.Database.MigrateAsync(stoppingToken);
            }

            var serverUrl = Environment.GetEnvironmentVariable("MARKETDATA_SERVER_URL") ?? "http://marketdata-server:5000";

            // allow plaintext http2 for local dev
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            using var channel = GrpcChannel.ForAddress(serverUrl);
            var client = new MarketData.MarketDataClient(channel);
            using var call = client.Subscribe();

            var instruments = (Environment.GetEnvironmentVariable("INSTRUMENTS") ?? "BTCUSDT,ETHUSDT").Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var ins in instruments) await call.RequestStream.WriteAsync(new SubscriptionRequest { InstrumentId = ins.Trim(), Unsubscribe = false });

            await foreach (var msg in call.ResponseStream.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = _sp.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ClientPersistenceDbContext>();
                    if (msg.PayloadCase == MarketDataMessage.PayloadOneofCase.Snapshot)
                    {
                        var s = msg.Snapshot;
                        db.Snapshots.Add(new SnapshotEntity { InstrumentId = s.InstrumentId, Sequence = s.Sequence, SnapshotJson = JsonSerializer.Serialize(s) });
                    }
                    else if (msg.PayloadCase == MarketDataMessage.PayloadOneofCase.Update)
                    {
                        var u = msg.Update;
                        db.Updates.Add(new UpdateEntity { InstrumentId = u.InstrumentId, Sequence = u.Sequence, UpdateJson = JsonSerializer.Serialize(u) });
                    }
                    await db.SaveChangesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to persist message");
                }
            }
        }
    }
}

}