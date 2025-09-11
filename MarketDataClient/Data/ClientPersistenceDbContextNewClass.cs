using System;
using Microsoft.EntityFrameworkCore;

namespace MarketDataClient.Data
{
    public class ClientPersistenceDbContext : DbContext
    {
        public ClientPersistenceDbContext(DbContextOptions<ClientPersistenceDbContext> options) : base(options) { }

        public DbSet<SnapshotEntity> Snapshots { get; set; }
        public DbSet<UpdateEntity> Updates { get; set; }
    }

    public class SnapshotEntity
    {
        public int Id { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public long Sequence { get; set; }
        public string SnapshotJson { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class UpdateEntity
    {
        public int Id { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public long Sequence { get; set; }
        public string UpdateJson { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
