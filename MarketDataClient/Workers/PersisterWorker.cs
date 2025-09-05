using System.Text.Json;
using Grpc.Net.Client;
using Market.Proto;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MarketDataClient.Data;
using Microsoft.EntityFrameworkCore;
using Grpc.Core;
using System.Net.Sockets;

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
            //Apply DB migrations (best-effort)
            try
            {
                using (var scope = _sp.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ClientPersistenceDbContext>();
                    await db.Database.MigrateAsync(stoppingToken);
                    _logger.LogInformation("Client DB migrations applied (persister).");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply migrations (persister). Continuing without DB.");
            }

            var serverUrl = Environment.GetEnvironmentVariable("MARKETDATA_SERVER_URL") ?? "http://localhost:5000";

            //allow plaintext http2 for local dev
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            //keep trying forever until stoppingToken requests cancellation
            int attempt = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                attempt++;
                try
                {
                    _logger.LogInformation("Persister attempting to connect to server (attempt {Attempt}) at {ServerUrl}", attempt, serverUrl);

                    using var channel = GrpcChannel.ForAddress(serverUrl);
                    var client = new MarketData.MarketDataClient(channel);
                    using var call = client.Subscribe();

                    var instruments = (Environment.GetEnvironmentVariable("INSTRUMENTS") ?? "BTCUSDT,ETHUSDT")
                                      .Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var ins in instruments)
                    {
                        await call.RequestStream.WriteAsync(new SubscriptionRequest { InstrumentId = ins.Trim(), Unsubscribe = false });
                        _logger.LogDebug("Persister subscribed to {Instrument}", ins.Trim());
                    }

                    //Reset attempt counter on successful connect
                    attempt = 0;
                    _logger.LogInformation("Persister connected and listening for messages.");

                    //Read stream until cancellation or error
                    while (await call.ResponseStream.MoveNext(stoppingToken))
                    {
                        var msg = call.ResponseStream.Current;
                        try
                        {
                            using var scope = _sp.CreateScope();
                            var db = scope.ServiceProvider.GetRequiredService<ClientPersistenceDbContext>();

                            if (msg.PayloadCase == MarketDataMessage.PayloadOneofCase.Snapshot)
                            {
                                var s = msg.Snapshot;
                                var ent = new SnapshotEntity
                                {
                                    InstrumentId = s.InstrumentId,
                                    Sequence = s.Sequence,
                                    SnapshotJson = JsonSerializer.Serialize(s),
                                    CreatedAt = DateTime.UtcNow
                                };

                                db.Snapshots.Add(ent);
                                await db.SaveChangesAsync(stoppingToken);

                                _logger.LogInformation("Persisted snapshot Id={Id} Instrument={Instrument} Seq={Seq}",
                                    ent.Id, ent.InstrumentId, ent.Sequence);
                            }
                            else if (msg.PayloadCase == MarketDataMessage.PayloadOneofCase.Update)
                            {
                                var u = msg.Update;
                                var ent = new UpdateEntity
                                {
                                    InstrumentId = u.InstrumentId,
                                    Sequence = u.Sequence,
                                    UpdateJson = JsonSerializer.Serialize(u),
                                    CreatedAt = DateTime.UtcNow
                                };

                                db.Updates.Add(ent);
                                await db.SaveChangesAsync(stoppingToken);

                                _logger.LogInformation("Persisted update Id={Id} Instrument={Instrument} Seq={Seq}",
                                    ent.Id, ent.InstrumentId, ent.Sequence);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to persist message (db error).");
                        }
                    }

                    //If response stream completes, loop and reconnect.
                    _logger.LogWarning("Persister response stream ended — will attempt reconnect.");
                }
                catch (OperationCanceledException) // shutdown requested
                {
                    _logger.LogInformation("Persister cancellation requested, exiting.");
                    break;
                }
                catch (RpcException rex) when (rex.StatusCode == Grpc.Core.StatusCode.Unavailable || rex.StatusCode == Grpc.Core.StatusCode.Internal)
                {
                    _logger.LogWarning(rex, "Persister RPC failure (status {Status}) — retrying.", rex.Status);
                }
                catch (SocketException sex)
                {
                    _logger.LogWarning(sex, "Persister socket error — retrying.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Persister worker terminated with exception — will retry.");
                }

                //exponential backoff with jitter
                var backoff = Math.Min(30, (int)Math.Pow(2, Math.Min(attempt, 6)));
                var jitter = new Random().Next(0, 1000);
                var delayMs = backoff * 1000 + jitter;
                _logger.LogInformation("Persister waiting {DelayMs}ms before reconnect attempt {Attempt}.", delayMs, attempt);
                try
                {
                    await Task.Delay(delayMs, stoppingToken);
                }
                catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("Persister worker stopped.");
        }
    }
}
