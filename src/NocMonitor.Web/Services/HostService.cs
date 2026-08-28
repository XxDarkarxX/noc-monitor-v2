using Microsoft.EntityFrameworkCore;
using NocMonitor.Data;
using NocMonitor.Data.Entities;
using Host = NocMonitor.Data.Entities.Host;

namespace NocMonitor.Web.Services;

/// <summary>
/// Host CRUD for the web panel. Creating/editing/deleting "hard" fields
/// (IP, URL, check type, etc.) only applies to Source=Manual: the weekly
/// sync (Phase 6) overwrites Source=Synced ones, so editing them here would
/// be pointless. Muting notifications is the one exception and applies to
/// any host.
/// </summary>
public sealed class HostService(NocMonitorDbContext db, ILogger<HostService> logger)
{
    public Task<List<Host>> GetAllAsync(CancellationToken cancellationToken = default) =>
        db.Hosts.AsNoTracking().OrderBy(h => h.Name).ToListAsync(cancellationToken);

    public Task<Host?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, cancellationToken);

    public async Task<Host> CreateAsync(
        string name,
        CheckType checkType,
        string? ip,
        string? httpUrl,
        int? intervalSecondsOverride,
        int? failThresholdOverride,
        CancellationToken cancellationToken = default)
    {
        var host = new Host
        {
            Name = name,
            Type = HostType.Server, // manual creation: Hpv/Vm are exclusive to the sync (Phase 6)
            Source = HostSource.Manual,
            CheckType = checkType,
            Ip = ip,
            HttpUrl = httpUrl,
            IntervalSecondsOverride = intervalSecondsOverride,
            FailThresholdOverride = failThresholdOverride,
        };

        db.Hosts.Add(host);
        await db.SaveChangesAsync(cancellationToken);
        return host;
    }

    public async Task UpdateAsync(
        int id,
        string name,
        CheckType checkType,
        string? ip,
        string? httpUrl,
        int? intervalSecondsOverride,
        int? failThresholdOverride,
        CancellationToken cancellationToken = default)
    {
        var host = await RequireManualHostAsync(id, cancellationToken);

        host.Name = name;
        host.CheckType = checkType;
        host.Ip = ip;
        host.HttpUrl = httpUrl;
        host.IntervalSecondsOverride = intervalSecondsOverride;
        host.FailThresholdOverride = failThresholdOverride;
        host.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var host = await RequireManualHostAsync(id, cancellationToken);
        db.Hosts.Remove(host);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Mute notifications: the only action that applies equally to Manual and Synced hosts.</summary>
    public async Task SetMuteAsync(
        int id, bool mutedIndefinitely, DateTime? mutedUntil, CancellationToken cancellationToken = default)
    {
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Host {id} does not exist.");

        host.MutedIndefinitely = mutedIndefinitely;
        host.MutedUntil = mutedUntil;
        host.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Manual Managed/Unmanaged (dashboard button): reuses the same
    /// IsMonitored that CheckSchedulerService already filters on, so an
    /// Unmanaged host stops being checked immediately, not just alerted on.
    /// Applies to any host, Manual or Synced — for a Synced one,
    /// HpvSyncService may reactivate it on the next run only (see the
    /// comment there).
    /// </summary>
    public async Task SetManagedAsync(int id, bool isManaged, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Host {id} does not exist.");
        var tFetch = sw.ElapsedMilliseconds;

        host.IsMonitored = isManaged;
        host.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        // [PERF]
        logger.LogInformation(
            "[PERF] SetManagedAsync({HostId}): fetch={FetchMs}ms save={SaveMs}ms total={TotalMs}ms",
            id, tFetch, sw.ElapsedMilliseconds - tFetch, sw.ElapsedMilliseconds);
    }

    private async Task<Host> RequireManualHostAsync(int id, CancellationToken cancellationToken)
    {
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Host {id} does not exist.");

        if (host.Source != HostSource.Manual)
            throw new InvalidOperationException("Synced hosts can't be edited or deleted from here.");

        return host;
    }
}
