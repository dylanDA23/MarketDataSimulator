using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace MarketDataServer.Data
{
    public class ServerPersistenceDbContext : DbContext
    {
        public ServerPersistenceDbContext(DbContextOptions<ServerPersistenceDbContext> options) : base(options) { }
        public DbSet<OrderBookSnapshotEntity> Snapshots { get; set; }
        public DbSet<OrderBookUpdateEntity> Updates { get; set; }
    }

    public class OrderBookSnapshotEntity
    {
        public int Id { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public long Sequence { get; set; }
        public string SnapshotJson { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class OrderBookUpdateEntity
    {
        public int Id { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public long Sequence { get; set; }
        public string UpdateJson { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
