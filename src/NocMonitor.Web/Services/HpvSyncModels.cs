namespace NocMonitor.Web.Services;

/// <summary>
/// DTOs for GET /api/hosts and GET /api/vms. Deserialized with
/// JsonNamingPolicy.SnakeCaseLower (see HpvSyncService), so these PascalCase
/// properties map on their own to the JSON's snake_case names (HostIp ->
/// host_ip, VmId -> vm_id, etc.) without needing [JsonPropertyName].
/// </summary>
internal sealed class HpvApiDto
{
    public string? Host { get; init; }
    public string? HostIp { get; init; }

    // "reachable" and "vm_count" come in the response but are deliberately
    // ignored: Phase 6 adds/updates ALL HPVs regardless of those values (the
    // API's reachable doesn't replace our own ping).
}

internal sealed class VmApiDto
{
    public string? VmId { get; init; }
    public string? Host { get; init; }
    public string? Name { get; init; }
    public string? VmFlags { get; init; }
    public string? State { get; init; }
    public string? IpAddresses { get; init; }

    // "critical_flag" exists in the API but always comes back 0 — not
    // reliable, deliberately not mapped here so nobody uses it by mistake.
    // The real criticality criterion is the "CRITICAL" token inside VmFlags.
}
