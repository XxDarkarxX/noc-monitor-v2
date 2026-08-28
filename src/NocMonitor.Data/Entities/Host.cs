namespace NocMonitor.Data.Entities;

public class Host
{
    public int Id { get; set; }

    public required string Name { get; set; }
    public HostType Type { get; set; } = HostType.Server;
    public HostSource Source { get; set; } = HostSource.Manual;

    // --- How it's checked ---
    public CheckType CheckType { get; set; } = CheckType.Icmp;
    public string? Ip { get; set; }
    public string? HttpUrl { get; set; }

    /// <summary>If this is a VM, the name of the HPV it runs on (for the Discord message).</summary>
    public string? ParentHostName { get; set; }

    /// <summary>
    /// The API's vm_id for VMs, or the host name for HPVs.
    /// Used to match on each sync run and avoid duplicates.
    /// Null for manually added hosts.
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// False for "Off" VMs or ones without a valid IP: they show up on the dashboard but aren't pinged.
    /// </summary>
    public bool IsMonitored { get; set; } = true;

    // --- Optional overrides (if null, the global config is used) ---
    public int? IntervalSecondsOverride { get; set; }
    public int? FailThresholdOverride { get; set; }

    // --- Manually mute notifications from the web ---
    public bool MutedIndefinitely { get; set; } = false;
    public DateTime? MutedUntil { get; set; }

    public bool IsMuted => MutedIndefinitely || (MutedUntil is { } until && until > DateTime.UtcNow);

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<CheckResult> CheckResults { get; set; } = new List<CheckResult>();
    public ICollection<Incident> Incidents { get; set; } = new List<Incident>();
}
