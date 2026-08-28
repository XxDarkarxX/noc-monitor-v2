namespace NocMonitor.Data.Entities;

/// <summary>A history row: the result of a single check against a host.</summary>
public class CheckResult
{
    public long Id { get; set; }

    public int HostId { get; set; }
    public Host? Host { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool Success { get; set; }
    public int? LatencyMs { get; set; }
    public string? ErrorMessage { get; set; }
}
