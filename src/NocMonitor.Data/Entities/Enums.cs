namespace NocMonitor.Data.Entities;

/// <summary>What kind of entity this host is, mainly for grouping on the dashboard.</summary>
public enum HostType
{
    Server,   // generic host added by hand (e.g. ISPs, servers outside the HPVs)
    Hpv,      // hypervisor, comes from /api/hosts
    Vm        // critical virtual machine, comes from /api/vms
}

/// <summary>How this host is checked.</summary>
public enum CheckType
{
    Icmp,
    Http
}

/// <summary>Where this record came from, so the sync never overwrites what was added by hand.</summary>
public enum HostSource
{
    Manual,
    Synced
}

/// <summary>What happened to a synced host in a sync run (see SyncLogEntry).</summary>
public enum SyncEventType
{
    Added,
    Deactivated
}
