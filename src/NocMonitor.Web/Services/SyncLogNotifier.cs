namespace NocMonitor.Web.Services;

/// <summary>
/// Tells Blazor Server components that the sync log (SyncLogEntry) changed —
/// a new entry from HpvSyncService, or someone marked one as seen — so the
/// header button updates its badge on its own via SignalR, no polling. Same
/// pattern as HostStatusNotifier.
/// </summary>
public sealed class SyncLogNotifier
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
