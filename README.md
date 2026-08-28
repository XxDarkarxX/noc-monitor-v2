# NOC Monitor v2

Redesign of NOC Monitor: ICMP + HTTP monitoring for servers, HPVs, and critical
VMs, with a live dashboard, database-backed history, a REST API, and Discord
alerts.

**Current status: Phase 7.** Check engine (ICMP/HTTP), web CRUD for hosts,
Discord alerts with periodic re-alerts, live dashboard with history, weekly
synchronization of critical HPVs/VMs against the internal API, and a Docker
deployment for the internal network.

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
`docker-compose.yml` — see "Deployment (Docker)" below.

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

## Deployment (Docker)

Meant to run inside a trusted internal network (not exposed to the
internet), so it's plain HTTP — no certificate to manage.

```bash
# 1. Copy the example env file and fill in real values
cp .env.example .env
# edit .env: ConnectionStrings__NocMonitorDb, Alerts__DiscordWebhook, HpvApi__BaseUrl

# 2. Build and start
docker compose up -d --build

# 3. Check it's up
curl http://localhost:8080/
docker compose logs -f web
```

`.env` is gitignored and must never be committed — it holds the real Discord
webhook and (if your internal API needs one) any API credential. Only
`.env.example` (no real values) is versioned, so anyone cloning the repo
knows what to fill in.

The SQLite database lives on the named volume `noc-monitor-data`, mounted at
`/app/data` inside the container. It survives `docker compose down` and
container recreates — it's only lost if the volume itself is deleted
(`docker compose down -v`).

**Migrating to another machine:**

```bash
# On the old machine: back up the volume to a tar file
docker run --rm -v noc-monitor-data:/data -v "$(pwd)":/backup \
  alpine tar czf /backup/noc-monitor-data.tar.gz -C /data .

# Copy noc-monitor-data.tar.gz + your .env to the new machine, then:
docker volume create noc-monitor-data
docker run --rm -v noc-monitor-data:/data -v "$(pwd)":/backup \
  alpine tar xzf /backup/noc-monitor-data.tar.gz -C /data
docker compose up -d
```

## Runbook

Quick reference for anyone operating this without prior context.

**View logs**

```bash
docker compose logs -f web        # follow live
docker compose logs --tail=200 web  # last 200 lines
```

**Restart the service**

```bash
docker compose restart web
```

**Where's the database?**

Inside the container at `/app/data/nocmonitor.db`, backed by the
`noc-monitor-data` Docker volume — see "Migrating to another machine" above
to back it up or move it. It has the host list, check history, and incidents;
losing it doesn't affect the internal HPV/VM API or Discord, just this app's
own state (the weekly sync will repopulate synced hosts on its own, but check
history and any manually-added hosts would be gone).

**Discord webhook stopped working**

Symptom: hosts go down/recover in the dashboard, but nothing posts to
Discord. Check the logs first — `docker compose logs web | grep -i discord`.
Two likely causes:

1. The webhook was deleted/regenerated on the Discord side. Fix: in Discord,
   go to the target channel → Edit Channel → Integrations → Webhooks, either
   restore or create a new one, copy its URL.
2. `.env` has the wrong key — the app reads `Alerts__DiscordWebhook`
   (**not** `Alerts__DiscordWebhookUrl`); a typo here fails silently (just a
   log warning, no crash).

After fixing `.env`, restart the container (env vars are only read at
startup):

```bash
docker compose up -d
```
