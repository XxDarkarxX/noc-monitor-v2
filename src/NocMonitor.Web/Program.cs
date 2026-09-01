using Microsoft.EntityFrameworkCore;
using NocMonitor.Alerts;
using NocMonitor.Core.Checkers;
using NocMonitor.Data;
using NocMonitor.Data.Entities;
using NocMonitor.Web.Components;
using NocMonitor.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Blazor Server ---
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// --- Check engine (Phase 2) ---
// Checkers resolved via keyed DI based on Host.CheckType, so the scheduler
// doesn't need a switch/if to pick an implementation.
builder.Services.AddHttpClient(nameof(HttpChecker));
builder.Services.AddKeyedSingleton<IChecker, PingChecker>(CheckType.Icmp);
builder.Services.AddKeyedSingleton<IChecker, HttpChecker>(CheckType.Http);
builder.Services.AddSingleton<HostStatusNotifier>();
builder.Services.AddHostedService<CheckSchedulerService>();

// --- Discord alerts (Phase 4) ---
// The webhook is read from "Alerts:DiscordWebhook" (user-secrets in dev,
// env var in production) inside DiscordAlertSender, never from
// appsettings.json.
builder.Services.AddHttpClient(nameof(DiscordAlertSender));
builder.Services.AddSingleton<IAlertSender, DiscordAlertSender>();

// --- Weekly HPV/VM sync (Phase 6) ---
// The API is internal and unauthenticated, so "HpvApi:BaseUrl" is enough
// (appsettings.json/env var, no user-secrets needed here). If it's not
// configured, HttpClient.BaseAddress stays null and HpvSyncService skips
// the run with a warning instead of failing.
builder.Services.AddHttpClient(nameof(HpvSyncService), (sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["HpvApi:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl))
        client.BaseAddress = new Uri(baseUrl);
    // Default HttpClient.Timeout is 100s - way too long for a UI button to
    // sit on "Syncing..." if the internal API becomes unreachable (see the
    // exception-filter comment in HpvSyncService.RunSyncAsync for the related
    // bug this made worse: a silent, unbounded hang with nothing logged).
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddSingleton<SyncLogNotifier>();
builder.Services.AddScoped<SyncLogService>();
// Explicit singleton (not just AddHostedService<T>) so HpvSyncService can be
// injected directly and RunSyncAsync triggered on demand from the UI (the
// "Sync now" button on /hosts), without waiting on the weekly cron.
builder.Services.AddSingleton<HpvSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HpvSyncService>());

// --- Database ---
// The connection string lives in appsettings.json (just the file path,
// nothing sensitive). Real secrets (Discord webhook, etc.) are read from
// env vars or user-secrets, never from here.
var connectionString = builder.Configuration.GetConnectionString("NocMonitorDb")
    ?? "Data Source=/app/data/nocmonitor.db";

// DefaultTimeout: without SQLite's default rollback-journal mode, a writer
// holds an exclusive lock on the whole file, so any other connection trying
// to write at the same time either waits (up to this many seconds, via
// sqlite3_busy_timeout) or throws "database is locked" - this bounds that
// wait instead of leaving it at the driver's 30s default. Paired with
// PRAGMA journal_mode=WAL below (readers never wait on a writer at all in
// WAL mode), this is what keeps a UI action (Mute, Managed/Unmanaged,
// manual sync) from queuing up behind CheckSchedulerService's writes during
// a burst - see its own comments for why that burst can be large.
var sqliteConnectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString)
{
    DefaultTimeout = 5,
}.ConnectionString;

builder.Services.AddDbContext<NocMonitorDbContext>(options =>
    options.UseSqlite(sqliteConnectionString));

// --- Web CRUD (Phase 3) ---
builder.Services.AddScoped<HostService>();

// --- Dashboard and history (Phase 5) ---
builder.Services.AddScoped<DashboardService>();

var app = builder.Build();

// Applies pending migrations at startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NocMonitorDbContext>();
    db.Database.Migrate();

    // WAL mode is stored in the database file's own header, not a
    // per-connection setting - it only has to be set once, ever, but
    // reissuing it on every startup is a cheap no-op and guarantees it's on
    // even for a pre-existing DB file from before this change. Unlike the
    // default rollback-journal mode, WAL lets readers (dashboard load, host
    // list) proceed without waiting on an in-progress writer at all - only
    // writer-vs-writer contention remains, which DefaultTimeout above bounds.
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
