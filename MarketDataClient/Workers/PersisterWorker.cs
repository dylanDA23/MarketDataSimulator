using System;
using System.IO;
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
using MarketDataClient.Services;
using System.Threading;
using System.Threading.Tasks;

namespace MarketDataClient.Workers
{
    public class PersisterWorker : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<PersisterWorker> _logger;
        private readonly string _persisterLogPath;

        public PersisterWorker(IServiceProvider sp, ILogger<PersisterWorker> logger)
        {
            _sp = sp;
            _logger = logger;
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? ".";
                _persisterLogPath = Path.Combine(home, "market-client-persister.log");
            }
            catch
            {
                _persisterLogPath = "market-client-persister.log";
            }
        }

        private void PersistToLocalFile(string text)
        {
            try
            {
                File.AppendAllText(_persisterLogPath, $"{DateTime.UtcNow:o} {text}\n");
            }
            catch
            {
                try { _logger.LogDebug("Unable to write to persister log file at {Path}", _persisterLogPath); } catch { }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Apply DB migrations 
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ClientPersistenceDbContext>();
                await db.Database.MigrateAsync(stoppingToken);
                _logger.LogInformation("Client DB migrations applied (persister).");
                PersistToLocalFile("Migrations applied");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply migrations (persister). Continuing without DB.");
                PersistToLocalFile($"Migrations FAILED: {ex.Message}");
            }

            var serverUrl = Environment.GetEnvironmentVariable("MARKETDATA_SERVER_URL") ?? "http://localhost:5000";

            
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            int attempt = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                attempt++;
                GrpcChannel? channel = null;
                AsyncDuplexStreamingCall<SubscriptionRequest, MarketDataMessage>? call = null;

                try
                {
                    _logger.LogInformation("Persister attempting to connect to server (attempt {Attempt}) at {ServerUrl}", attempt, serverUrl);
                    PersistToLocalFile($"Attempting connect to {serverUrl} (attempt {attempt})");

                    channel = GrpcChannel.ForAddress(serverUrl);
                    var client = new MarketData.MarketDataClient(channel);
                    call = client.Subscribe();

                    var instruments = (Environment.GetEnvironmentVariable("INSTRUMENTS") ?? "BTCUSDT,ETHUSDT")
                                      .Split(',', StringSplitOptions.RemoveEmptyEntries);

                    foreach (var ins in instruments)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        await call.RequestStream.WriteAsync(new SubscriptionRequest { InstrumentId = ins.Trim(), Unsubscribe = false });
                        _logger.LogDebug("Persister subscribed to {Instrument}", ins.Trim());
                        PersistToLocalFile($"Subscribed {ins.Trim()}");
                    }

                    if (stoppingToken.IsCancellationRequested) break;

                    attempt = 0;
                    _logger.LogInformation("Persister connected and listening for messages.");
                    PersistToLocalFile("Connected and listening");

                    // Read stream until cancellation or error
                    while (await call.ResponseStream.MoveNext(stoppingToken))
                    {
                        var msg = call.ResponseStream.Current;

                        // persistent minimal log(what kind of payload we received)
                        try { PersistToLocalFile($"Received message: PayloadCase={msg.PayloadCase}"); } catch { }

                        try
                        {
                            using var scope = _sp.CreateScope();
                            var db = scope.ServiceProvider.GetRequiredService<ClientPersistenceDbContext>();

                            // Shared in-memory hub 
                            var hub = _sp.GetService<MarketDataService>();

                            if (msg.PayloadCase == MarketDataMessage.PayloadOneofCase.Snapshot)
                            {
                                var s = msg.Snapshot;
                                // Extra persistent log to make snapshot reception explicit
                                PersistToLocalFile($"Received SNAPSHOT Instrument={s.InstrumentId} Seq={s.Sequence}");
                                var ent = new SnapshotEntity
                                {
                                    InstrumentId = s.InstrumentId,
                                    Sequence = s.Sequence,
                                    SnapshotJson = JsonSerializer.Serialize(s),
                                    CreatedAt = DateTime.UtcNow
                                };

                                try
                                {
                                    db.Snapshots.Add(ent);
                                    await db.SaveChangesAsync(stoppingToken);
                                    _logger.LogInformation("Persisted snapshot Id={Id} Instrument={Instrument} Seq={Seq}",
                                        ent.Id, ent.InstrumentId, ent.Sequence);
                                    PersistToLocalFile($"Persisted SNAPSHOT Id={ent.Id} Instrument={ent.InstrumentId} Seq={ent.Sequence}");
                                }
                                catch (Exception saveEx)
                                {
                                    _logger.LogError(saveEx, "Failed to persist snapshot to DB.");
                                    PersistToLocalFile($"FAILED to persist SNAPSHOT Instrument={ent.InstrumentId} Seq={ent.Sequence} Error={saveEx.Message}");
                                }

                                try { hub?.ApplySnapshot(s); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to apply snapshot to hub"); }
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

                                try
                                {
                                    db.Updates.Add(ent);
                                    await db.SaveChangesAsync(stoppingToken);
                                    _logger.LogInformation("Persisted update Id={Id} Instrument={Instrument} Seq={Seq}",
                                        ent.Id, ent.InstrumentId, ent.Sequence);
                                    PersistToLocalFile($"Persisted UPDATE Id={ent.Id} Instrument={ent.InstrumentId} Seq={ent.Sequence}");
                                }
                                catch (Exception saveEx)
                                {
                                    _logger.LogError(saveEx, "Failed to persist update to DB.");
                                    PersistToLocalFile($"FAILED to persist UPDATE Instrument={ent.InstrumentId} Seq={ent.Sequence} Error={saveEx.Message}");
                                }

                                try { hub?.ApplyUpdate(u); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to apply update to hub"); }
                            }
                            else if (msg.PayloadCase == MarketDataMessage.PayloadOneofCase.EmptySnapshot)
                            {
                                // EmptySnapshot is sent when a client is unsubscribed 
                                PersistToLocalFile($"Received EMPTY_SNAPSHOT Instrument={msg.EmptySnapshot.InstrumentId}");
                            }
                            else
                            {
                                PersistToLocalFile($"Received unknown payload case: {msg.PayloadCase}");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            PersistToLocalFile("OperationCanceledException while persisting");
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to persist message (db error).");
                            PersistToLocalFile($"Failed to persist message: {ex.Message}");
                        }
                    }

                    _logger.LogWarning("Persister response stream ended — will attempt reconnect.");
                    PersistToLocalFile("Response stream ended - will attempt reconnect");
                }
                catch (OperationCanceledException) // shutdown requested
                {
                    _logger.LogInformation("Persister cancellation requested, cleaning up...");
                    PersistToLocalFile("Cancellation requested - cleaning up");
                    break;
                }
                catch (RpcException rex) when (rex.StatusCode == Grpc.Core.StatusCode.Unavailable || rex.StatusCode == Grpc.Core.StatusCode.Internal)
                {
                    _logger.LogWarning(rex, "Persister RPC failure (status {Status}) — retrying.", rex.Status);
                    PersistToLocalFile($"RPC failure: {rex.Status}");
                }
                catch (SocketException sex)
                {
                    _logger.LogWarning(sex, "Persister socket error — retrying.");
                    PersistToLocalFile($"Socket error: {sex.Message}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Persister worker terminated with exception — will retry.");
                    PersistToLocalFile($"Unhandled exception: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (call != null)
                        {
                            try { await call.RequestStream.CompleteAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to CompleteAsync persister request stream (ignored)."); PersistToLocalFile($"CompleteAsync failed: {ex.Message}"); }
                        }
                    }
                    catch { }

                    try { channel?.Dispose(); } catch { }
                }

                // exponential backoff with jitter
                var backoff = Math.Min(30, (int)Math.Pow(2, Math.Min(attempt, 6)));
                var jitter = new Random().Next(0, 1000);
                var delayMs = backoff * 1000 + jitter;
                _logger.LogInformation("Persister waiting {DelayMs}ms before reconnect attempt {Attempt}.", delayMs, attempt);
                PersistToLocalFile($"Waiting {delayMs}ms before reconnect attempt {attempt}");
                try
                {
                    await Task.Delay(delayMs, stoppingToken);
                }
                catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("Persister worker stopped.");
            PersistToLocalFile("Persister worker stopped.");
        }
    }
}
