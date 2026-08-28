using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NocMonitor.Data.Entities;

namespace NocMonitor.Alerts;

/// <summary>
/// Sends alerts to a Discord channel via webhook. The webhook never lives in
/// appsettings.json: it's read from "Alerts:DiscordWebhook" (user-secrets in
/// dev, the ALERTS__DISCORDWEBHOOK env var in production). If it's not
/// configured, logs a warning and sends nothing (so it doesn't break the dev
/// environment of someone who hasn't set up the webhook yet).
/// </summary>
public sealed class DiscordAlertSender(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<DiscordAlertSender> logger) : IAlertSender
{
    public Task SendDownAsync(Host host, Incident incident, CancellationToken cancellationToken = default)
    {
        var reminder = incident.AlertsSent > 1;
        var since = FormatDuration(DateTime.UtcNow - incident.StartedAt);
        var message = reminder
            ? $"@everyone 🔴 **{host.Name}**{ParentSuffix(host)} is still down (down for {since}, reminder #{incident.AlertsSent})."
            : $"@everyone 🔴 **{host.Name}**{ParentSuffix(host)} is down (down for {since}).";

        return PostAsync(message, cancellationToken);
    }

    public Task SendRecoveredAsync(Host host, Incident incident, CancellationToken cancellationToken = default)
    {
        var downtime = FormatDuration((incident.ResolvedAt ?? DateTime.UtcNow) - incident.StartedAt);
        var message = $"✅ **{host.Name}**{ParentSuffix(host)} recovered (was down for {downtime}).";

        return PostAsync(message, cancellationToken);
    }

    private static string ParentSuffix(Host host) =>
        host.Type == HostType.Vm && !string.IsNullOrWhiteSpace(host.ParentHostName)
            ? $" running on {host.ParentHostName}"
            : string.Empty;

    private async Task PostAsync(string message, CancellationToken cancellationToken)
    {
        var webhookUrl = configuration["Alerts:DiscordWebhook"];
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            logger.LogWarning("Alerts:DiscordWebhook is not configured; skipping the alert send");
            return;
        }

        var client = httpClientFactory.CreateClient(nameof(DiscordAlertSender));
        using var response = await client.PostAsJsonAsync(webhookUrl, new { content = message }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{(int)span.TotalSeconds}s";
    }
}
