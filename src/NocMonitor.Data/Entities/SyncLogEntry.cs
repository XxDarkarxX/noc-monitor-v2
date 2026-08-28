namespace NocMonitor.Data.Entities;

/// <summary>
/// An entry in the visible HPV/VM sync log (Phase 6): a real addition or
/// deactivation, never a no-op update. Written by HpvSyncService in the
/// same run that updates the Hosts; the dashboard reads it for the header's
/// "New"/"History" panel.
/// </summary>
public class SyncLogEntry
{
    public long Id { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public required string HostName { get; set; }
    public HostType Type { get; set; }
    public SyncEventType EventType { get; set; }

    /// <summary>True once someone has marked it as seen from the "New" panel.</summary>
    public bool IsAcknowledged { get; set; } = false;
}
