namespace NocMonitor.Web.Services;

/// <summary>
/// Tells Blazor Server components that some host's status may have
/// changed, so the dashboard (Phase 5) re-renders on its own via SignalR —
/// without the component having to poll. Deliberately doesn't say WHICH
/// host changed: for the number of hosts on this NOC, it's fine for the
/// dashboard to just reload everything on each event.
///
/// Throttled to at most 1 firing per ThrottleWindow: CheckSchedulerService
/// calls NotifyChanged once per EACH host checked (with ~40 hosts at
/// second-level intervals, that's several firings per second). Without
/// throttling, every firing triggers a full dashboard reload
/// (GetHostStatusesAsync, with the CheckResults table growing unbounded) on
/// the Blazor Server circuit's single sync context — that saturates it and
/// leaves any other UI interaction (e.g. the sync panel) queued for several
/// seconds behind dashboard reloads it doesn't even depend on. The first
/// firing in a burst goes out immediately (leading edge); ones that arrive
/// during the window collapse into a single final firing (trailing edge) so
/// the latest state isn't lost.
/// </summary>
public sealed class HostStatusNotifier
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

        Changed?.Invoke();
    }
}
