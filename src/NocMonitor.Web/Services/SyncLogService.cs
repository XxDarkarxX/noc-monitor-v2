using Microsoft.EntityFrameworkCore;
using NocMonitor.Data;
using NocMonitor.Data.Entities;

namespace NocMonitor.Web.Services;

/// <summary>
/// Reading/acknowledging the sync log for the header panel. Only exposes
/// SyncLogEntry — those rows already come filtered to Hpv/Vm from
/// HpvSyncService, so there's no need to filter again here.
/// </summary>
public sealed class SyncLogService(NocMonitorDbContext db, SyncLogNotifier notifier, ILogger<SyncLogService> logger)
{
    public Task<List<SyncLogEntry>> GetUnacknowledgedAsync(CancellationToken cancellationToken = default) =>
        db.SyncLogEntries.AsNoTracking()
            .Where(e => !e.IsAcknowledged)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(cancellationToken);

    public Task<List<SyncLogEntry>> GetAllAsync(CancellationToken cancellationToken = default) =>
        db.SyncLogEntries.AsNoTracking()
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(cancellationToken);

    public Task<int> GetUnacknowledgedCountAsync(CancellationToken cancellationToken = default) =>
        db.SyncLogEntries.CountAsync(e => !e.IsAcknowledged, cancellationToken);

    public async Task AcknowledgeAsync(long id, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var entry = await db.SyncLogEntries.FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"SyncLogEntry {id} does not exist.");
        var tFetch = sw.ElapsedMilliseconds;

        entry.IsAcknowledged = true;
        await db.SaveChangesAsync(cancellationToken);
        var tSave = sw.ElapsedMilliseconds;

        notifier.NotifyChanged();

        // [PERF]
        logger.LogInformation(
            "[PERF] AcknowledgeAsync({Id}): fetch={FetchMs}ms save={SaveMs}ms notify={NotifyMs}ms total={TotalMs}ms",
            id, tFetch, tSave - tFetch, sw.ElapsedMilliseconds - tSave, sw.ElapsedMilliseconds);
    }
}
