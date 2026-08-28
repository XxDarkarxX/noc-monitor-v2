namespace NocMonitor.Core.Checkers;

/// <summary>
/// Raw result of a single check (ping or http) against a host.
/// Not the database entity: that lives in NocMonitor.Data.
/// </summary>
public sealed record CheckOutcome
{
    public required bool Success { get; init; }
    public int? LatencyMs { get; init; }
    public string? ErrorMessage { get; init; }

    public static CheckOutcome Ok(int latencyMs) =>
        new() { Success = true, LatencyMs = latencyMs };

    public static CheckOutcome Fail(string? error = null) =>
        new() { Success = false, ErrorMessage = error };
}
