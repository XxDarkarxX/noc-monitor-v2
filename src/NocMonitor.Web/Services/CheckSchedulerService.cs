using Microsoft.EntityFrameworkCore;
using NocMonitor.Alerts;
using NocMonitor.Core.Checkers;
using NocMonitor.Data;
using NocMonitor.Data.Entities;
using Host = NocMonitor.Data.Entities.Host;

namespace NocMonitor.Web.Services;

/// <summary>
/// Schedules checks for all hosts with IsMonitored=true and persists every
/// CheckResult. Re-reads the host list on every tick (instead of caching it
/// once at startup) so additions/removals from the web CRUD (Phase 3) take
/// effect without restarting the process.
///
/// Also opens/closes Incidents and fires alerts (Phase 4): unlike v1, which
/// alerted 3 times and then went completely silent while the host stayed
/// down, here a reminder is resent while the incident stays open — every
/// 30s for the first 3 alerts (initial intrusive burst) and every 5 minutes
/// after that, based on AlertsSent.
/// </summary>
public sealed class CheckSchedulerService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    HostStatusNotifier statusNotifier,
    ILogger<CheckSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>Initial intrusive burst: the first 3 alerts of an Incident go out every 30s.</summary>
    private static readonly TimeSpan InitialReminderInterval = TimeSpan.FromSeconds(30);

    /// <summary>From the 4th alert on, they space out to every 5 minutes until resolved.</summary>
    private static readonly TimeSpan SteadyReminderInterval = TimeSpan.FromMinutes(5);

    /// <summary>How many alerts fall in the initial burst before switching to the spaced-out interval.</summary>
    private const int InitialBurstCount = 3;

    private const int MaxConcurrentChecks = 20;

    // Only ExecuteAsync's main loop writes here; the parallel checks don't
    // touch it, so it doesn't need a lock.
    private readonly Dictionary<int, DateTime> _nextRunAt = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var defaultIntervalSeconds = configuration.GetValue("Monitor:IntervalSeconds", 5);
        var defaultFailThreshold = configuration.GetValue("Monitor:FailThreshold", 5);
        using var throttle = new SemaphoreSlim(MaxConcurrentChecks);
        using var timer = new PeriodicTimer(TickInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            List<Host> dueHosts;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NocMonitorDbContext>();
                var hosts = await db.Hosts.AsNoTracking()
                    .Where(h => h.IsMonitored)
                    .ToListAsync(stoppingToken);

                var now = DateTime.UtcNow;

                var currentIds = hosts.Select(h => h.Id).ToHashSet();
                foreach (var staleId in _nextRunAt.Keys.Where(id => !currentIds.Contains(id)).ToList())
                    _nextRunAt.Remove(staleId);

                dueHosts = hosts.Where(h => !_nextRunAt.TryGetValue(h.Id, out var next) || next <= now).ToList();

                foreach (var host in dueHosts)
                {
                    var intervalSeconds = host.IntervalSecondsOverride ?? defaultIntervalSeconds;
                    _nextRunAt[host.Id] = now.AddSeconds(intervalSeconds);
                }
            }

            if (dueHosts.Count == 0)
                continue;

            // Checks run concurrently (I/O-bound, no DB access) but persistence
            // is batched into a single transaction below. Root cause of the
            // [PERF] audit: N due hosts used to mean N concurrent
            // SaveChangesAsync calls, each opening its own connection - SQLite
            // only allows one writer at a time, so they serialized and each
            // one waited on all the ones ahead of it (~150ms/contender,
            // measured up to 3s+ with ~20 concurrent). That contention is what
            // was also stalling unrelated reads/writes from the UI (dashboard
            // reload, SetManagedAsync, AcknowledgeAsync) whenever they landed
            // during a burst. One batched write per tick turns that from
            // O(dueHosts) lock acquisitions into 1, regardless of host count.
            var outcomes = await CheckHostsAsync(dueHosts, throttle, stoppingToken);
            await PersistAndEvaluateAsync(outcomes, defaultFailThreshold, stoppingToken);
        }
    }

    private async Task<List<(Host Host, CheckOutcome Outcome)>> CheckHostsAsync(
        List<Host> dueHosts, SemaphoreSlim throttle, CancellationToken stoppingToken)
    {
        var tasks = dueHosts.Select(async host =>
        {
            await throttle.WaitAsync(stoppingToken);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var checker = scope.ServiceProvider.GetRequiredKeyedService<IChecker>(host.CheckType);

                var target = new CheckTarget { Ip = host.Ip ?? string.Empty, HttpUrl = host.HttpUrl };
                var outcome = await checker.CheckAsync(target, stoppingToken);
                return ((Host Host, CheckOutcome? Outcome))(host, outcome);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error checking host {HostId} ({HostName})", host.Id, host.Name);
                return (host, null);
            }
            finally
            {
                throttle.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results
            .Where(r => r.Outcome is not null)
            .Select(r => (r.Host, r.Outcome!))
            .ToList();
    }

    private async Task PersistAndEvaluateAsync(
        List<(Host Host, CheckOutcome Outcome)> outcomes, int defaultFailThreshold, CancellationToken stoppingToken)
    {
        if (outcomes.Count == 0)
            return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NocMonitorDbContext>();

            // Tracked (not AsNoTracking) so the LastCheck* updates below get
            // picked up by the same SaveChangesAsync as the CheckResult inserts.
            var hostIds = outcomes.Select(o => o.Host.Id).ToList();
            var trackedHosts = await db.Hosts
                .Where(h => hostIds.Contains(h.Id))
                .ToDictionaryAsync(h => h.Id, stoppingToken);

            foreach (var (host, outcome) in outcomes)
            {
                db.CheckResults.Add(new CheckResult
                {
                    HostId = host.Id,
                    Success = outcome.Success,
                    LatencyMs = outcome.LatencyMs,
                    ErrorMessage = outcome.ErrorMessage,
                });

                // Denormalized snapshot so the dashboard doesn't need to query
                // CheckResults for "latest per host" - see comment on Host.
                if (trackedHosts.TryGetValue(host.Id, out var trackedHost))
                {
                    trackedHost.LastCheckAt = DateTime.UtcNow;
                    trackedHost.LastCheckSuccess = outcome.Success;
                    trackedHost.LastCheckLatencyMs = outcome.LatencyMs;
                    trackedHost.LastCheckError = outcome.ErrorMessage;
                }
            }

            // [PERF] one write for the whole tick's batch, instead of one per host.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await db.SaveChangesAsync(stoppingToken);
            var tSave = sw.ElapsedMilliseconds;

            // Sequential on purpose: same DbContext, and each host's query here
            // depends on its own CheckResult already being committed above -
            // batching the writes but running these concurrently too would just
            // reintroduce the same per-connection contention for no benefit
            // (incident writes are rare - only on an actual state change - so
            // serializing them costs nothing noticeable).
            foreach (var (host, outcome) in outcomes)
            {
                try
                {
                    var failThreshold = host.FailThresholdOverride ?? defaultFailThreshold;
                    await EvaluateIncidentAsync(db, host, outcome.Success, failThreshold, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error evaluating incident for host {HostId} ({HostName})", host.Id, host.Name);
                }
            }

            if (tSave > 50 || sw.ElapsedMilliseconds - tSave > 50)
            {
                logger.LogInformation(
                    "[PERF] PersistAndEvaluateAsync: batchSave({Count})={SaveMs}ms evaluateIncidents={IncidentsMs}ms",
                    outcomes.Count, tSave, sw.ElapsedMilliseconds - tSave);
            }

            // The dashboard (Phase 5) re-renders on its own via this event, no
            // polling - once per tick now instead of once per host.
            statusNotifier.NotifyChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error persisting check results for {Count} host(s)", outcomes.Count);
        }
    }

    /// <summary>
    /// Opens/closes the host's Incident based on the check result and fires
    /// the corresponding alert (or the periodic reminder if the incident is
    /// still open). The send itself is fired without awaiting it
    /// (fire-and-forget with its own scope) so it doesn't block the
    /// scheduler's throttle while Discord responds.
    /// </summary>
    private async Task EvaluateIncidentAsync(NocMonitorDbContext db, Host host, bool success, int failThreshold, CancellationToken stoppingToken)
    {
        var openIncident = await db.Incidents
            .Where(i => i.HostId == host.Id && i.ResolvedAt == null)
            .OrderByDescending(i => i.StartedAt)
            .FirstOrDefaultAsync(stoppingToken);

        if (success)
        {
            if (openIncident is null)
                return;

            openIncident.ResolvedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(stoppingToken);

            FireAlert(host, openIncident, isRecovery: true, stoppingToken);
            return;
        }

        if (openIncident is null)
        {
            var recentResults = await db.CheckResults
                .Where(r => r.HostId == host.Id)
                .OrderByDescending(r => r.Timestamp)
                .Take(failThreshold)
                .ToListAsync(stoppingToken);

            // Not enough history yet, or one of the last results succeeded:
            // hasn't crossed the FailThreshold.
            if (recentResults.Count < failThreshold || recentResults.Any(r => r.Success))
                return;

            openIncident = new Incident { HostId = host.Id, AlertsSent = 1, LastAlertAt = DateTime.UtcNow };
            db.Incidents.Add(openIncident);
            await db.SaveChangesAsync(stoppingToken);

            FireAlert(host, openIncident, isRecovery: false, stoppingToken);
            return;
        }

        var reminderInterval = openIncident.AlertsSent < InitialBurstCount ? InitialReminderInterval : SteadyReminderInterval;
        var sinceLastAlert = DateTime.UtcNow - (openIncident.LastAlertAt ?? openIncident.StartedAt);
        if (sinceLastAlert < reminderInterval)
            return;

        openIncident.AlertsSent++;
        openIncident.LastAlertAt = DateTime.UtcNow;
        await db.SaveChangesAsync(stoppingToken);

        FireAlert(host, openIncident, isRecovery: false, stoppingToken);
    }

    private void FireAlert(Host host, Incident incident, bool isRecovery, CancellationToken stoppingToken)
    {
        if (host.IsMuted)
            return;

        _ = SendAlertAsync(host, incident, isRecovery, stoppingToken);
    }

    private async Task SendAlertAsync(Host host, Incident incident, bool isRecovery, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var alertSender = scope.ServiceProvider.GetRequiredService<IAlertSender>();
            if (isRecovery)
                await alertSender.SendRecoveredAsync(host, incident, stoppingToken);
            else
                await alertSender.SendDownAsync(host, incident, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error sending alert for host {HostId} ({HostName})", host.Id, host.Name);
        }
    }
}
