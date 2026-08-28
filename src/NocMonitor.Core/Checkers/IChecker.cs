namespace NocMonitor.Core.Checkers;

/// <summary>
/// Minimal data a checker needs to do its job.
/// Passed as a parameter instead of the EF Core Host entity so that
/// NocMonitor.Core doesn't need to depend on NocMonitor.Data.
/// </summary>
public sealed record CheckTarget
{
    public required string Ip { get; init; }
    public string? HttpUrl { get; init; }
    public int TimeoutMs { get; init; } = 1000;
}

/// <summary>
/// A checker knows how to do a single kind of verification (ping, http, etc).
/// The scheduling engine (Phase 2) decides which one to use based on the host.
/// </summary>
public interface IChecker
{
    Task<CheckOutcome> CheckAsync(CheckTarget target, CancellationToken cancellationToken = default);
}
