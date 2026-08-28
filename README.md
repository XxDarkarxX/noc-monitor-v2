# NOC Monitor v2

Redesign of NOC Monitor: ICMP + HTTP monitoring for servers, HPVs, and critical
VMs, with a live dashboard, database-backed history, a REST API, and Discord
alerts.

**Current status: Phase 6.** Check engine (ICMP/HTTP), web CRUD for hosts,
Discord alerts with periodic re-alerts, live dashboard with history, and
weekly synchronization of critical HPVs/VMs against the internal API. Still
missing Docker/`.env` for production (Phase 7).

## Structure

```
src/
  NocMonitor.Core/      Check contracts (IChecker) - no dependencies
  NocMonitor.Data/      EF Core + SQLite: Host, CheckResult, Incident entities
  NocMonitor.Alerts/    Notification contract (IAlertSender)
  NocMonitor.Web/       ASP.NET Core + Blazor Server, hosts everything else
```

## Running locally

Requires the .NET 10 SDK.

```bash
# 1. Create the initial migration (only the first time, or when entities change)
cd src/NocMonitor.Data
dotnet ef migrations add InitialCreate --startup-project ../NocMonitor.Web

# 2. Run the app (applies the migration automatically on startup)
cd ../NocMonitor.Web
dotnet run
```

By default the DB lives at `/app/data/nocmonitor.db` per `appsettings.json`.
Locally, override the path with `appsettings.Development.json` (it's in
`.gitignore`, not committed) or with the environment variable:

```bash
export ConnectionStrings__NocMonitorDb="Data Source=./nocmonitor.dev.db"
```

## Secrets

**Never go in `appsettings.json` or any versioned file.** The Discord webhook
and any HPV/VM API credentials are loaded from an environment variable or
`dotnet user-secrets` in development:

```bash
cd src/NocMonitor.Web
dotnet user-secrets set "Alerts:DiscordWebhook" "https://discord.com/api/webhooks/..."
```

In production (Docker), they go in an `.env` (gitignored) referenced from
`docker-compose.yml` — added in Phase 7.

## HPV/VM sync (Phase 6)

`HpvSyncService` runs on its own, on Sundays 3am Costa Rica time (cron
`0 3 * * SUN` via Cronos), against an internal unauthenticated API. Its base
URL isn't a secret, it goes in `appsettings.json`/an env var:

```bash
export HpvApi__BaseUrl="http://your-internal-api:8080/"
```

If it's not configured, the service skips the run (with a warning in the log)
instead of failing. Everything it creates/updates ends up with
`Source=Synced` and never touches `Source=Manual` hosts.
