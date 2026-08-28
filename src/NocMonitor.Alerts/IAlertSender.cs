using NocMonitor.Data.Entities;

namespace NocMonitor.Alerts;

/// <summary>Sends a status-change notification for a host. See <see cref="DiscordAlertSender"/>.</summary>
public interface IAlertSender
{
    Task SendDownAsync(Host host, Incident incident, CancellationToken cancellationToken = default);
    Task SendRecoveredAsync(Host host, Incident incident, CancellationToken cancellationToken = default);
}
