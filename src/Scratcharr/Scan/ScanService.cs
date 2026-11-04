using Microsoft.Extensions.Options;
using Scratcharr.Clients;
using Scratcharr.Options;

namespace Scratcharr.Scan;

public sealed record ScanResult
{
    public DateTime FinishedAt { get; init; }
    public TimeSpan Took { get; init; }
    public IReadOnlyList<ItemReport> Items { get; init; } = [];
    public Evidence Evidence { get; init; } = new();
    public SettingsReport Settings { get; init; } = new();
    public long FreeBytes { get; init; }
    public long TotalBytes { get; init; }
    public string? Error { get; init; }

    public ItemReport? Find(string id) => Items.FirstOrDefault(i => i.Id == id);
}

/// <summary>
/// Reads the clients and walks /downloads every few minutes (and when asked), then keeps the
/// result for the pages. One scan at a time.
/// </summary>
public sealed class ScanService(
    IOptions<ScratcharrOptions> options,
    Store store,
    QbittorrentClient qbittorrent,
    SabnzbdClient sabnzbd,
    ArrClient arr,
    ILogger<ScanService> logger) : BackgroundService
{
    private readonly ScratcharrOptions _options = options.Value;
    private readonly SemaphoreSlim _scanNow = new(0);

    public ScanResult? Last { get; private set; }
    public bool Scanning { get; private set; }

    public event Action? Changed;

    public void ScanNow() => _scanNow.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunAsync(stoppingToken);
            try
            {
                await _scanNow.WaitAsync(TimeSpan.FromMinutes(_options.ScanMinutes), stoppingToken);
                // Several clicks in a row make one scan.
                while (_scanNow.CurrentCount > 0)
                {
                    await _scanNow.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Scanning = true;
        Changed?.Invoke();
        var started = DateTime.UtcNow;
        try
        {
            var qbt = qbittorrent.ReadAsync(cancellationToken);
            var sab = sabnzbd.ReadAsync(cancellationToken);
            var arrs = Enum.GetValues<ArrApp>().ToDictionary(a => a, a => arr.ReadAsync(a, cancellationToken));
            await Task.WhenAll(arrs.Values.Cast<Task>().Append(qbt).Append(sab));
            var evidence = new Evidence
            {
                Qbittorrent = await qbt,
                Sabnzbd = await sab,
                Arr = arrs.ToDictionary(a => a.Key, a => a.Value.Result)
            };

            var categories = CategoryNames(_options, evidence);
            var disk = await Task.Run(() => DiskScanner.Scan(_options, categories, cancellationToken), cancellationToken);
            var now = DateTime.UtcNow;
            var seen = store.MarkSeen(disk.Select(d => d.Path).ToList(), now);
            var items = disk
                .Select(d => Classifier.Classify(d, evidence, now, seen.GetValueOrDefault(d.Path, now)))
                .ToList();
            var (free, total) = Space(_options.DownloadsRoot);
            Last = new ScanResult
            {
                FinishedAt = DateTime.UtcNow,
                Took = DateTime.UtcNow - started,
                Items = items,
                Evidence = evidence,
                Settings = SettingsCheck.Build(_options, evidence, items),
                FreeBytes = free,
                TotalBytes = total
            };
            logger.LogInformation("Scan: {Count} items, {Strays} to review, in {Seconds:0.0} s",
                items.Count, items.Count(i => i.Verdict.NeedsReview()), Last.Took.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Scan failed");
            Last = (Last ?? new ScanResult()) with { Error = e.Message, FinishedAt = DateTime.UtcNow };
        }
        finally
        {
            Scanning = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Folder names that hold one item per entry: the configured ones plus every client category.</summary>
    internal static HashSet<string> CategoryNames(ScratcharrOptions options, Evidence evidence)
    {
        var names = new HashSet<string>(options.CategoryFolders, StringComparer.Ordinal);
        if (evidence.Qbittorrent.State is { } qbt)
        {
            foreach (var (name, path) in qbt.Categories)
            {
                names.Add(name);
                if (path.Length > 0)
                {
                    names.Add(Path.GetFileName(path));
                }
            }
        }

        if (evidence.Sabnzbd.State is { } sab)
        {
            foreach (var (name, dir) in sab.Categories.Where(c => c.Key != "*"))
            {
                names.Add(name);
                if (dir.Length > 0)
                {
                    names.Add(Path.GetFileName(dir));
                }
            }
        }

        names.Remove("");
        return names;
    }

    private static (long Free, long Total) Space(string path)
    {
        try
        {
            var drive = new DriveInfo(path);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }
}
