using Microsoft.Extensions.Options;
using Scratcharr.Options;
using Scratcharr.Scan;

namespace Scratcharr.Clean;

/// <summary>
/// Once an hour: deletes what has been in the recycle folder long enough, applies the auto rules, sends the low space
/// alert and the weekly summary when they are due.
/// </summary>
public sealed class CleanupService(
    IOptions<ScratcharrOptions> options,
    Store store,
    ScanService scans,
    Cleaner cleaner,
    LiveSettings live,
    ILogger<CleanupService> logger) : BackgroundService
{
    public const string LastSentKey = "summary.lastSent";
    public const string SpaceAlertSentKey = "alert.space.lastSent";

    private readonly ScratcharrOptions _options = options.Value;

    public DateTime? LastSent => store.Setting(LastSentKey) is { } text
        ? DateTime.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind)
        : null;

    /// <summary>When the next summary goes out (local time), or null without ntfy.</summary>
    public DateTime? NextDue => _options.Ntfy.Configured ? Summary.LastDue(_options.Ntfy, DateTime.Now).AddDays(7) : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the first scan finish before judging anything.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (count, bytes) = cleaner.Purge(DateTime.UtcNow);
                if (count > 0)
                {
                    logger.LogInformation("Purge: deleted {Count} recycled items, {Size}", count, Format.Size(bytes));
                }

                await AutoCleanAsync(stoppingToken);
                await SpaceAlertIfDueAsync(stoppingToken);
                await SummaryIfDueAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Clean-up round failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>When the auto rules last ran (UTC), for the Settings page.</summary>
    public DateTime? LastAutoRun { get; private set; }

    /// <summary>
    /// Applies the auto rules to the last scan; the Cleaner scans again and re-checks every item before moving it.
    /// Returns what happened (empty when nothing was due or changes are off).
    /// </summary>
    public async Task<IReadOnlyList<CleanOutcome>> AutoCleanAsync(CancellationToken cancellationToken)
    {
        var rules = live.AutoRules;
        if (!_options.AllowChanges || !rules.Any(r => r.Enabled) || scans.Last is not { Error: null } scan)
        {
            return [];
        }

        LastAutoRun = DateTime.UtcNow;
        var due = AutoRules.Due(scan, rules, _options, DateTime.UtcNow);
        if (due.Count == 0)
        {
            return [];
        }

        var reasons = due.ToDictionary(d => d.Report.Item.Path, d => AutoRules.Reason(d.Rule));
        var outcomes = await cleaner.CleanAsync(reasons.Keys.ToList(), cancellationToken, reasons);
        logger.LogInformation("Auto rules: recycled {Done} of {Due} items", outcomes.Count(o => o.Ok), due.Count);
        foreach (var failed in outcomes.Where(o => !o.Ok))
        {
            logger.LogWarning("Auto rules left {Path}: {Message}", failed.Path, failed.Message);
        }

        return outcomes;
    }

    /// <summary>When the low space alert last went out (UTC); cleared once there is enough space again.</summary>
    public DateTime? SpaceAlertSent => store.Setting(SpaceAlertSentKey) is { } text
        ? DateTime.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind)
        : null;

    private async Task SpaceAlertIfDueAsync(CancellationToken cancellationToken)
    {
        var settings = live.SpaceAlert;
        if (!_options.Ntfy.Configured || scans.Last is not { Error: null, TotalBytes: > 0 } scan)
        {
            return;
        }

        if (!SpaceAlert.Low(scan, settings))
        {
            if (SpaceAlertSent != null)
            {
                store.SetSetting(SpaceAlertSentKey, null);
            }

            return;
        }

        if (SpaceAlert.Due(scan, settings, SpaceAlertSent, DateTime.UtcNow))
        {
            await SendSpaceAlertAsync(cancellationToken);
        }
    }

    /// <summary>Sends the low space alert now (also the Send a test button); returns the error, or null.</summary>
    public async Task<string?> SendSpaceAlertAsync(CancellationToken cancellationToken, bool test = false)
    {
        if (!_options.Ntfy.Configured)
        {
            return "Set Scratcharr__Ntfy__Url (NTFY_URL) to send alerts.";
        }

        if (scans.Last is not { Error: null, TotalBytes: > 0 } scan)
        {
            return "There is no good scan with the free space yet.";
        }

        var settings = live.SpaceAlert;
        var text = SpaceAlert.Text(scan, settings, store.Recycled(), _options.RecycleDays);
        try
        {
            await Summary.SendAsync(_options.Ntfy, test ? $"Test: {text}" : text, cancellationToken,
                "Scratcharr: downloads space is low", "warning,floppy_disk", test ? null : "high");
            if (!test)
            {
                store.SetSetting(SpaceAlertSentKey, DateTime.UtcNow.ToString("O"));
                logger.LogWarning("Low space alert sent: {Free} free", Format.Size(scan.FreeBytes));
            }

            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            logger.LogWarning("Low space alert not sent: {Message}", e.Message);
            return e is TaskCanceledException ? "ntfy did not answer in time." : e.Message;
        }
    }

    private async Task SummaryIfDueAsync(CancellationToken cancellationToken)
    {
        if (!_options.Ntfy.Configured || scans.Last is not { Error: null })
        {
            return;
        }

        var due = Summary.LastDue(_options.Ntfy, DateTime.Now).ToUniversalTime();
        if (LastSent is not { } last)
        {
            // First start: the next one goes out on the next due day, not right now.
            store.SetSetting(LastSentKey, DateTime.UtcNow.ToString("O"));
            return;
        }

        if (last < due)
        {
            await SendNowAsync(cancellationToken);
        }
    }

    /// <summary>Sends the summary now (also the Send now button); returns the error, or null.</summary>
    public async Task<string?> SendNowAsync(CancellationToken cancellationToken)
    {
        if (!_options.Ntfy.Configured)
        {
            return "Set Scratcharr__Ntfy__Url (NTFY_URL) to send the summary.";
        }

        if (scans.Last is not { Error: null } scan)
        {
            return "There is no good scan to summarise yet.";
        }

        var text = Summary.Text(scan, store.Recycled(), store.Actions(1000, DateTime.UtcNow.AddDays(-7)), DateTime.UtcNow);
        try
        {
            await Summary.SendAsync(_options.Ntfy, text, cancellationToken);
            store.SetSetting(LastSentKey, DateTime.UtcNow.ToString("O"));
            logger.LogInformation("Weekly summary sent to ntfy");
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            logger.LogWarning("Weekly summary not sent: {Message}", e.Message);
            return e is TaskCanceledException ? "ntfy did not answer in time." : e.Message;
        }
    }
}
