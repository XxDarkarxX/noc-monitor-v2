namespace NocMonitor.Data.Entities;

/// <summary>
/// An outage: from when a host crosses the FailThreshold until it recovers.
/// Persisted (unlike v1's in-memory HostState) so a container restart
/// doesn't lose the state or the history.
/// </summary>
public class Incident
{
    public long Id { get; set; }

    public int HostId { get; set; }
    public Host? Host { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    public int AlertsSent { get; set; } = 0;
    public DateTime? LastAlertAt { get; set; }

    public bool IsResolved => ResolvedAt != null;
}
