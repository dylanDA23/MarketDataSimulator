using MarketDataClient;
using MarketDataClient.Data;
using MarketDataClient.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
var clientConn = Environment.GetEnvironmentVariable("CLIENT_POSTGRES_CONN") ?? "Host=postgres_client;Port=5432;Username=postgres;Password=postgres;Database=clientdb";
services.AddDbContext<ClientPersistenceDbContext>(opts => opts.UseNpgsql(clientConn));
services.AddHostedService<PersisterWorker>();
var provider = services.BuildServiceProvider();

var lifetime = provider.GetRequiredService<IHostApplicationLifetime>();
// Run the hosted service manually (Console app)
var host = Host.CreateDefaultBuilder()
    .ConfigureServices((ctx, s) =>
    {
        s.AddDbContext<ClientPersistenceDbContext>(opts => opts.UseNpgsql(clientConn));
        s.AddHostedService<PersisterWorker>();
    })
    .Build();

await host.RunAsync();
//
//