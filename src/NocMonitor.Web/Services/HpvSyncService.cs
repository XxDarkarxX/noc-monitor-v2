using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cronos;
using Microsoft.EntityFrameworkCore;
using NocMonitor.Data;
using NocMonitor.Data.Entities;
using Host = NocMonitor.Data.Entities.Host;

namespace NocMonitor.Web.Services;

/// <summary>
/// Syncs HPVs and critical VMs from the internal API (GET /api/hosts, GET
/// /api/vms), running weekly on Sundays 3am Costa Rica time. Everything it
/// creates/updates ends up with Source=Synced; it never touches
/// Source=Manual hosts (not even if the name/vm_id matches — the match
/// below always filters by Source=Synced, so at most it would create a
/// duplicate Synced instead of overwriting the Manual one).
/// </summary>
public sealed class HpvSyncService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    SyncLogNotifier syncLogNotifier,
    ILogger<HpvSyncService> logger) : BackgroundService
{
    private static readonly CronExpression Schedule = CronExpression.Parse("0 3 * * SUN");

    // Costa Rica doesn't observe daylight saving time (UTC-6 year-round), so
    // a fixed offset is more robust here than relying on the OS having the
    // "America/Costa_Rica" tz loaded.
    private static readonly TimeZoneInfo CostaRica =
        TimeZoneInfo.CreateCustomTimeZone("Costa Rica", TimeSpan.FromHours(-6), "Costa Rica Time", "Costa Rica Time");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = Schedule.GetNextOccurrence(DateTimeOffset.UtcNow, CostaRica);
            if (next is null)
            {
                logger.LogWarning("Could not compute the next HPV/VM sync run; stopping the BackgroundService.");
                return;
            }

            var delay = next.Value - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await RunSyncAsync(stoppingToken);
        }
    }

    /// <summary>A single run's logic, separated from ExecuteAsync so it can be invoked directly in tests.</summary>
    public async Task RunSyncAsync(CancellationToken stoppingToken)
    {
        var client = httpClientFactory.CreateClient(nameof(HpvSyncService));
        if (client.BaseAddress is null)
        {
            logger.LogWarning("HpvApi:BaseUrl is not configured; skipping the HPV/VM sync.");
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NocMonitorDbContext>();

            var hpvDtos = await client.GetFromJsonAsync<List<HpvApiDto>>("api/hosts", JsonOptions, stoppingToken) ?? [];
            var vmDtos = await client.GetFromJsonAsync<List<VmApiDto>>("api/vms", JsonOptions, stoppingToken) ?? [];

            var (hpvAdded, hpvUpdated) = await SyncHpvsAsync(db, hpvDtos, stoppingToken);
            var (vmAdded, vmUpdated, vmDeactivated) = await SyncVmsAsync(db, vmDtos, stoppingToken);

            await db.SaveChangesAsync(stoppingToken);

            // Notify on every successful run, not just when there were
            // additions/deactivations: an update-only run still changes
            // fields (Name/Ip/IsMonitored) that HostsPage's table shows, and
            // in practice almost every routine run is update-only (added/
            // deactivated only happen when a host actually appears/vanishes
            // upstream) — gating this on added+deactivated>0 meant /hosts
            // basically never auto-refreshed after a real-world sync.
            // SyncLogPanel.OnChanged (the other subscriber) just recomputes
            // the unread count and reloads its own tab if open, both cheap
            // no-ops when there's nothing new, so firing unconditionally
            // doesn't cost it anything either.
            syncLogNotifier.NotifyChanged();

            logger.LogInformation(
                "HPV/VM sync completed — HPVs: {HpvAdded} added, {HpvUpdated} updated. Critical VMs: {VmAdded} added, {VmUpdated} updated, {VmDeactivated} deactivated.",
                hpvAdded, hpvUpdated, vmAdded, vmUpdated, vmDeactivated);
        }
        // Deliberately NOT just "ex is not OperationCanceledException": if the
        // internal HTTP request times out (HttpClient.Timeout, configured in
        // Program.cs), it throws TaskCanceledException - which IS-A
        // OperationCanceledException, so a naive filter here would silently
        // swallow it with no log line at all. That previously left the "Sync
        // now" button stuck on "Syncing..." forever with zero evidence in the
        // logs whenever HpvApi was unreachable in a way that hangs instead of
        // refusing immediately (e.g. a network with no route back - exactly
        // what happens if a Docker container can't reach an internal LAN IP).
        // Only skip logging when OUR OWN stoppingToken (app shutdown) is what
        // requested the cancellation - that one legitimately isn't an error.
        catch (Exception ex) when (!(ex is OperationCanceledException) || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Error running the HPV/VM sync");
        }
    }

    private static async Task<(int Added, int Updated)> SyncHpvsAsync(
        NocMonitorDbContext db, List<HpvApiDto> hpvs, CancellationToken stoppingToken)
    {
        int added = 0, updated = 0;
        var now = DateTime.UtcNow;

        foreach (var dto in hpvs)
        {
            if (string.IsNullOrWhiteSpace(dto.Host))
                continue;

            // ExternalId = the HPV's name for this type (see comment on Host.ExternalId).
            var existing = await db.Hosts.FirstOrDefaultAsync(
                h => h.ExternalId == dto.Host && h.Source == HostSource.Synced && h.Type == HostType.Hpv,
                stoppingToken);

            if (existing is null)
            {
                db.Hosts.Add(new Host
                {
                    Name = dto.Host,
                    Type = HostType.Hpv,
                    Source = HostSource.Synced,
                    CheckType = CheckType.Icmp,
                    Ip = dto.HostIp,
                    ExternalId = dto.Host,
                    IsMonitored = true, // regardless of the API's "reachable"/"vm_count" — our own ping decides
                });
                db.SyncLogEntries.Add(new SyncLogEntry
                {
                    HostName = dto.Host,
                    Type = HostType.Hpv,
                    EventType = SyncEventType.Added,
                });
                added++;
            }
            else
            {
                existing.Name = dto.Host;
                existing.Ip = dto.HostIp;
                // An HPV has no "Off" state in this API (unlike the VMs
                // below): if it was manually Unmanaged and the sync finds it
                // again, it reactivates with no exception.
                existing.IsMonitored = true;
                existing.UpdatedAt = now;
                updated++;
            }
        }

        return (added, updated);
    }

    private static async Task<(int Added, int Updated, int Deactivated)> SyncVmsAsync(
        NocMonitorDbContext db, List<VmApiDto> vms, CancellationToken stoppingToken)
    {
        int added = 0, updated = 0, deactivated = 0;
        var now = DateTime.UtcNow;
        var criticalVmIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dto in vms)
        {
            if (string.IsNullOrWhiteSpace(dto.VmId) || string.IsNullOrWhiteSpace(dto.Name) || !HasCriticalFlag(dto.VmFlags))
                continue;

            criticalVmIds.Add(dto.VmId);

            var ip = ExtractFirstIPv4(dto.IpAddresses);
            var isRunning = string.Equals(dto.State, "Running", StringComparison.Ordinal);
            var isMonitored = isRunning && ip is not null;

            var existing = await db.Hosts.FirstOrDefaultAsync(
                h => h.ExternalId == dto.VmId && h.Source == HostSource.Synced && h.Type == HostType.Vm,
                stoppingToken);

            if (existing is null)
            {
                db.Hosts.Add(new Host
                {
                    Name = dto.Name,
                    Type = HostType.Vm,
                    Source = HostSource.Synced,
                    CheckType = CheckType.Icmp,
                    Ip = ip,
                    ParentHostName = dto.Host,
                    ExternalId = dto.VmId,
                    IsMonitored = isMonitored,
                });
                db.SyncLogEntries.Add(new SyncLogEntry
                {
                    HostName = dto.Name,
                    Type = HostType.Vm,
                    EventType = SyncEventType.Added,
                });
                added++;
            }
            else
            {
                existing.Name = dto.Name;
                existing.Ip = ip;
                existing.ParentHostName = dto.Host;
                // Overwrites any manual Unmanaged with the API's real state: it
                // reactivates on its own unless the VM is really Off (or has no
                // IP), same as it already worked before the manual toggle existed.
                existing.IsMonitored = isMonitored;
                existing.UpdatedAt = now;
                updated++;
            }
        }

        // VMs that were already Synced and monitored, but this run didn't
        // touch them (lost the CRITICAL flag or disappeared from the API):
        // they aren't deleted (that would lose the CheckResult/Incident
        // history), just deactivated.
        var previouslyMonitored = await db.Hosts
            .Where(h => h.Type == HostType.Vm && h.Source == HostSource.Synced && h.IsMonitored)
            .ToListAsync(stoppingToken);

        foreach (var host in previouslyMonitored)
        {
            if (host.ExternalId is not null && criticalVmIds.Contains(host.ExternalId))
                continue;

            host.IsMonitored = false;
            host.UpdatedAt = now;
            db.SyncLogEntries.Add(new SyncLogEntry
            {
                HostName = host.Name,
                Type = HostType.Vm,
                EventType = SyncEventType.Deactivated,
            });
            deactivated++;
        }

        return (added, updated, deactivated);
    }

    private static bool HasCriticalFlag(string? vmFlagsCsv) =>
        !string.IsNullOrWhiteSpace(vmFlagsCsv) &&
        vmFlagsCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains("CRITICAL", StringComparer.Ordinal);

    /// <summary>First valid IPv4 from a comma-separated list; discards IPv6 (including fe80:: link-local) and empty entries.</summary>
    private static string? ExtractFirstIPv4(string? ipAddressesCsv)
    {
        if (string.IsNullOrWhiteSpace(ipAddressesCsv))
            return null;

        foreach (var raw in ipAddressesCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (IPAddress.TryParse(raw, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
                return raw;
        }

        return null;
    }
}
