namespace NocMonitor.Web.Services;

/// <summary>
/// Tells Blazor Server components that some host's status may have
/// changed, so the dashboard (Phase 5) re-renders on its own via SignalR —
/// without the component having to poll. Deliberately doesn't say WHICH
/// host changed: for the number of hosts on this NOC, it's fine for the
/// dashboard to just reload everything on each event.
///
/// Throttled to at most 1 firing per ThrottleWindow: CheckSchedulerService
/// now calls NotifyChanged once per tick (not once per host - see
/// PersistAndEvaluateAsync), but the throttle stays as a cheap safety net.
/// The first firing in a burst goes out immediately (leading edge); ones
/// that arrive during the window collapse into a single final firing
/// (trailing edge) so the latest state isn't lost.
///
/// [PERF audit]: this used to matter a lot more than the throttle alone
/// suggests. GetHostStatusesAsync's "latest check per host" query degraded
/// from ~25ms to ~550-600ms as CheckResults grew past ~1.5M rows (fixed by
/// denormalizing onto Host - see its comment), and CheckSchedulerService
/// used to run one SaveChangesAsync per due host concurrently, which
/// serialized on SQLite's single-writer lock and could take 3s+ under load
/// (fixed by batching - see PersistAndEvaluateAsync). Both together meant
/// every firing of this event could occupy the circuit's single-threaded
/// dispatcher for over a second, delaying unrelated clicks (e.g. the sync
/// panel) queued behind it on the same circuit. Measured before/after in
/// git history; if either of those numbers pull >100ms in production,
/// look here first.
/// </summary>
public sealed class HostStatusNotifier(ILogger<HostStatusNotifier> logger)
{
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private DateTime _lastFiredAt = DateTime.MinValue;
    private Timer? _trailingTimer;

    public event Action? Changed;

    public void NotifyChanged()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var sinceLastFire = now - _lastFiredAt;

            if (sinceLastFire >= ThrottleWindow)
            {
                _lastFiredAt = now;
                _trailingTimer?.Dispose();
                _trailingTimer = null;
            }
            else
            {
                // There's already been a recent firing; if there's no
                // trailing timer scheduled yet for the rest of this window,
                // schedule one. Any other NotifyChanged calls that arrive in
                // the meantime don't do anything else (a firing will happen
                // at the end of the window anyway).
                _trailingTimer ??= new Timer(FireTrailing, null, ThrottleWindow - sinceLastFire, Timeout.InfiniteTimeSpan);
                return;
            }
        }

        // [PERF] leading-edge fire: subscribers' handlers run synchronously
        // off this call, on whatever thread called NotifyChanged (the
        // scheduler's background thread). Debug level: useful for a future
        // audit, too frequent (up to 1/s) for permanent Information logging.
        logger.LogDebug("[PERF] HostStatusNotifier firing (leading edge) at {Time:HH:mm:ss.ffffff}", DateTime.Now);
        Changed?.Invoke();
    }

    private void FireTrailing(object? state)
    {
        lock (_lock)
        {
            _lastFiredAt = DateTime.UtcNow;
            _trailingTimer?.Dispose();
            _trailingTimer = null;
        }

        logger.LogDebug("[PERF] HostStatusNotifier firing (trailing edge) at {Time:HH:mm:ss.ffffff}", DateTime.Now);
        Changed?.Invoke();
    }
}
