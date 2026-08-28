using Microsoft.EntityFrameworkCore;
using NocMonitor.Data;
using NocMonitor.Data.Entities;
using Host = NocMonitor.Data.Entities.Host;

namespace NocMonitor.Web.Services;

/// <summary>Visual status of a host on the dashboard. Not persisted: derived on every read.</summary>
public enum HostStatus
{
    Unmonitored,
    Up,
    Warning,
    Down,
}

public sealed record HostStatusInfo(
    Host Host,
    HostStatus Status,
    DateTime? LastCheckAt,
    int? LastLatencyMs,
    string? LastErrorMessage,
    Incident? OpenIncident);

/// <summary>
/// Data for the live dashboard and per-host history (Phase 5). The
/// Up/Warning/Down status doesn't live on the Host entity: it's computed
/// here from the latest CheckResult and whether there's an open Incident.
/// Warning = the last check failed but hasn't crossed the FailThreshold yet
/// (no open Incident, it's "flapping"). Down = open Incident (threshold
/// crossed, confirmed outage) — see EvaluateIncidentAsync in
/// CheckSchedulerService.
/// </summary>
public sealed class DashboardService(NocMonitorDbContext db, ILogger<DashboardService> logger)
{
    public async Task<List<HostStatusInfo>> GetHostStatusesAsync(CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var hosts = await db.Hosts.AsNoTracking().OrderBy(h => h.Name).ToListAsync(cancellationToken);
        var tHosts = sw.ElapsedMilliseconds;

        var openIncidents = await db.Incidents.AsNoTracking()
            .Where(i => i.ResolvedAt == null)
            .ToDictionaryAsync(i => i.HostId, cancellationToken);
        var tIncidents = sw.ElapsedMilliseconds;

        // "Latest check per host" used to come from a correlated
        // MAX(Timestamp) query over CheckResults — that table grows forever
        // (one row per host per check interval), so the query degraded from
        // ~25ms at ~70k rows to ~550-600ms at ~1.5M rows measured in this dev
        // session ([PERF] audit). CheckSchedulerService now writes a snapshot
        // of the latest result directly onto the Host row (see comment
        // there), so this is just reading fields already in `hosts` above —
        // O(1) regardless of how large CheckResults gets.
        var result = hosts.Select(host =>
        {
            openIncidents.TryGetValue(host.Id, out var openIncident);

            var status = !host.IsMonitored
                ? HostStatus.Unmonitored
                : openIncident is not null
                    ? HostStatus.Down
                    : host.LastCheckAt is null
                        ? HostStatus.Unmonitored
                        : host.LastCheckSuccess == true ? HostStatus.Up : HostStatus.Warning;

            return new HostStatusInfo(host, status, host.LastCheckAt, host.LastCheckLatencyMs, host.LastCheckError, openIncident);
        }).ToList();

        // [PERF audit]: called by every dashboard load AND every
        // HostStatusNotifier-triggered refresh (up to 1/s) - Debug level to
        // avoid permanent log spam now that this is O(1) in CheckResults size
        // again; raise the level if a future audit needs the breakdown.
        logger.LogDebug(
            "[PERF] GetHostStatusesAsync: hosts={HostsMs}ms incidents={IncidentsMs}ms projection={ProjectionMs}ms total={TotalMs}ms hostCount={HostCount}",
            tHosts, tIncidents - tHosts, sw.ElapsedMilliseconds - tIncidents, sw.ElapsedMilliseconds, hosts.Count);

        return result;
    }

    public Task<Host?> GetHostAsync(int hostId, CancellationToken cancellationToken = default) =>
        db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == hostId, cancellationToken);

    public Task<List<Incident>> GetIncidentsAsync(int hostId, int take = 50, CancellationToken cancellationToken = default) =>
        db.Incidents.AsNoTracking()
            .Where(i => i.HostId == hostId)
            .OrderByDescending(i => i.StartedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

    public Task<List<CheckResult>> GetRecentChecksAsync(int hostId, int take = 100, CancellationToken cancellationToken = default) =>
        db.CheckResults.AsNoTracking()
            .Where(c => c.HostId == hostId)
            .OrderByDescending(c => c.Timestamp)
            .Take(take)
            .ToListAsync(cancellationToken);

    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{(int)span.TotalSeconds}s";
    }
}
