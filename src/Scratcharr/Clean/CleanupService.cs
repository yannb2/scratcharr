using Microsoft.Extensions.Options;
using Scratcharr.Options;
using Scratcharr.Scan;

namespace Scratcharr.Clean;

/// <summary>Once an hour: deletes what has been in the recycle folder long enough, and sends the weekly summary when it is due.</summary>
public sealed class CleanupService(
    IOptions<ScratcharrOptions> options,
    Store store,
    ScanService scans,
    Cleaner cleaner,
    ILogger<CleanupService> logger) : BackgroundService
{
    public const string LastSentKey = "summary.lastSent";

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
