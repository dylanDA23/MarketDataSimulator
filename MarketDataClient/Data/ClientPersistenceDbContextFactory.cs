using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MarketDataClient.Data
{
    public class ClientPersistenceDbContextFactory : IDesignTimeDbContextFactory<ClientPersistenceDbContext>
    {
        public ClientPersistenceDbContext CreateDbContext(string[] args)
        {
            var conn = Environment.GetEnvironmentVariable("CLIENT_POSTGRES_CONN")
                       ?? Environment.GetEnvironmentVariable("SERVER_POSTGRES_CONN")
                       ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=marketdb";

            var options = new DbContextOptionsBuilder<ClientPersistenceDbContext>()
                .UseNpgsql(conn, b => b.EnableRetryOnFailure())
                .Options;

            return new ClientPersistenceDbContext(options);
        }
    }
}
