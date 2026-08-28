using Microsoft.EntityFrameworkCore;
using NocMonitor.Data.Entities;

namespace NocMonitor.Data;

public class NocMonitorDbContext(DbContextOptions<NocMonitorDbContext> options) : DbContext(options)
{
    public DbSet<Host> Hosts => Set<Host>();
    public DbSet<CheckResult> CheckResults => Set<CheckResult>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<SyncLogEntry> SyncLogEntries => Set<SyncLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Host>(entity =>
        {
            entity.HasIndex(h => h.ExternalId);
            entity.HasIndex(h => new { h.Type, h.Source });
        });

        modelBuilder.Entity<CheckResult>(entity =>
        {
            entity.HasOne(c => c.Host)
                  .WithMany(h => h.CheckResults)
                  .HasForeignKey(c => c.HostId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Index for per-host history queries ordered by date
            entity.HasIndex(c => new { c.HostId, c.Timestamp });
        });

        modelBuilder.Entity<Incident>(entity =>
        {
            entity.HasOne(i => i.Host)
                  .WithMany(h => h.Incidents)
                  .HasForeignKey(i => i.HostId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(i => new { i.HostId, i.ResolvedAt });
        });

        modelBuilder.Entity<SyncLogEntry>(entity =>
        {
            entity.HasIndex(e => e.IsAcknowledged);
            entity.HasIndex(e => e.Timestamp);
        });
    }
}
