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

builder.Services.AddDbContext<NocMonitorDbContext>(options =>
    options.UseSqlite(connectionString));

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
