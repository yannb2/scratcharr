using Scratcharr.Clean;
using Arrkit.Hosting;
using Arrkit.Logging;
using System.Globalization;
using Scratcharr;
using Scratcharr.Clients;
using Scratcharr.Components;
using Scratcharr.Jobs;
using Scratcharr.Options;
using Scratcharr.Scan;
using Scratcharr.Tools;

// Numbers and dates the same way everywhere (1.5 GB, not 1,5 GB).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// ASP.NET's data-protection keys (antiforgery tokens) in /config/keys, so they survive an update.
DataProtectionKeys.PersistKeysIn(builder, Path.GetDirectoryName(builder.Configuration["Scratcharr:DatabasePath"] ?? "/config/scratcharr.db") ?? "/config", "Scratcharr");

builder.Services.AddOptions<ScratcharrOptions>()
    .Bind(builder.Configuration.GetSection(ScratcharrOptions.Section))
    .PostConfigure(o => o.WithDefaults());

// The last lines in memory for the Logs page (arrkit, shared with Ripparr, Salvarr, Specarr).
var memoryLog = new MemoryLog();
builder.Logging.AddProvider(new MemoryLoggerProvider(memoryLog));
builder.Services.AddSingleton(memoryLog);

builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<LiveSettings>();
builder.Services.AddScoped<Toasts>();
builder.Services.AddSingleton<QbittorrentClient>();
builder.Services.AddSingleton<SabnzbdClient>();
builder.Services.AddSingleton<ArrClient>();
builder.Services.AddSingleton<ScanService>();
builder.Services.AddHostedService(services => services.GetRequiredService<ScanService>());
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService(services => services.GetRequiredService<JobQueue>());
builder.Services.AddSingleton<Cleaner>();
builder.Services.AddSingleton<CleanupService>();
builder.Services.AddHostedService(services => services.GetRequiredService<CleanupService>());
builder.Services.AddSingleton<ToolStatusService>();
builder.Services.AddHostedService(services => services.GetRequiredService<ToolStatusService>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/health", () => Results.Ok("ok"));

// Counts for a dashboard (Homepage's custom API widget). Read-only, no key.
app.MapGet("/api/stats", (ScanService scans, ToolStatusService tools, JobQueue jobs, Cleaner cleaner) =>
    Results.Ok(Stats.From(scans.Last, scans.Scanning, tools, jobs.OpenCount, cleaner.Recycled())));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
