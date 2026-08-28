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
public sealed class DashboardService(NocMonitorDbContext db)
{
    public async Task<List<HostStatusInfo>> GetHostStatusesAsync(CancellationToken cancellationToken = default)
    {
        var hosts = await db.Hosts.AsNoTracking().OrderBy(h => h.Name).ToListAsync(cancellationToken);

        var openIncidents = await db.Incidents.AsNoTracking()
            .Where(i => i.ResolvedAt == null)
            .ToDictionaryAsync(i => i.HostId, cancellationToken);

        // GroupBy(HostId)+OrderByDescending+First scanned the ENTIRE table on
        // every call — with CheckResults growing unbounded on a NOC that's
        // been running a while, it kept getting slower (~140ms measured with
        // ~70k rows). The correlated MAX(Timestamp) filter does use the
        // existing (HostId, Timestamp) index, ~O(hosts) instead of O(rows in
        // CheckResults) — dropped to ~25ms with the same table.
        //
        // GroupBy client-side over the result (at most a handful of rows per
        // host) instead of ToDictionaryAsync directly: two CheckResults for
        // the same host with an identical Timestamp would make
        // ToDictionaryAsync blow up on a duplicate key — unlikely but not
        // impossible.
        var latestChecksRows = await db.CheckResults.AsNoTracking()
            .Where(c => c.Timestamp == db.CheckResults
                .Where(c2 => c2.HostId == c.HostId)
                .Max(c2 => c2.Timestamp))
            .ToListAsync(cancellationToken);
        var latestChecks = latestChecksRows.GroupBy(c => c.HostId).ToDictionary(g => g.Key, g => g.First());

        return hosts.Select(host =>
        {
            latestChecks.TryGetValue(host.Id, out var lastCheck);
            openIncidents.TryGetValue(host.Id, out var openIncident);

            var status = !host.IsMonitored
                ? HostStatus.Unmonitored
                : openIncident is not null
                    ? HostStatus.Down
                    : lastCheck is null
                        ? HostStatus.Unmonitored
                        : lastCheck.Success ? HostStatus.Up : HostStatus.Warning;

            return new HostStatusInfo(host, status, lastCheck?.Timestamp, lastCheck?.LatencyMs, lastCheck?.ErrorMessage, openIncident);
        }).ToList();
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
