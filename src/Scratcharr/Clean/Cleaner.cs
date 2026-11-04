using Microsoft.Extensions.Options;
using Scratcharr.Clients;
using Scratcharr.Jobs;
using Scratcharr.Options;
using Scratcharr.Scan;

namespace Scratcharr.Clean;

/// <summary>What happened to one item of a Clean up.</summary>
public sealed record CleanOutcome(string Path, bool Ok, string Message);

/// <summary>
/// Recycle, Restore and Delete. Nothing is deleted straight away: Recycle moves an item to the recycle folder (a
/// rename on the same mount), and only the Recycle page or the purge after RecycleDays deletes it. Before acting it
/// scans again and re-applies the rules, so an item a client picked up in the meantime is left alone. Every change
/// goes to the action log.
/// </summary>
public sealed class Cleaner(
    IOptions<ScratcharrOptions> options,
    Store store,
    ScanService scans,
    QbittorrentClient qbittorrent,
    SabnzbdClient sabnzbd,
    ILogger<Cleaner> logger)
{
    private readonly ScratcharrOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Raised after anything in the recycle folder changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<RecycledItem> Recycled() => store.Recycled();

    public IReadOnlyList<ActionEntry> Actions(int limit = 100) => store.Actions(limit);

    /// <summary>Scans again, then recycles each item the rules still allow (removing its torrent first when it has one).</summary>
    public async Task<IReadOnlyList<CleanOutcome>> CleanAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (!_options.AllowChanges)
        {
            return paths.Select(p => new CleanOutcome(p, false, UnpackPlan.ChangesOff)).ToList();
        }

        await _lock.WaitAsync(cancellationToken);
        var outcomes = new List<CleanOutcome>();
        try
        {
            await scans.RunAsync(cancellationToken);
            var scan = scans.Last;
            if (scan == null || scan.Error != null)
            {
                return paths.Select(p => new CleanOutcome(p, false, $"The scan before cleaning failed: {scan?.Error}")).ToList();
            }

            foreach (var path in paths)
            {
                var report = scan.Items.FirstOrDefault(i => i.Item.Path == path);
                if (report == null)
                {
                    outcomes.Add(new CleanOutcome(path, false, "It is not there any more."));
                    continue;
                }

                var offer = CleanPlan.Offer(report, _options);
                if (!offer.Allowed)
                {
                    outcomes.Add(new CleanOutcome(path, false, offer.Text));
                    continue;
                }

                outcomes.Add(await CleanOneAsync(report, offer, cancellationToken));
            }
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
        scans.ScanNow();
        return outcomes;
    }

    private async Task<CleanOutcome> CleanOneAsync(ItemReport report, CleanOffer offer, CancellationToken cancellationToken)
    {
        var item = report.Item;
        var notes = new List<string>();
        try
        {
            if (offer.Kind == CleanKind.RemoveTorrent)
            {
                var names = string.Join(", ", report.Torrents.Select(t => t.Name));
                await qbittorrent.RemoveAsync(report.Torrents.Select(t => t.Hash).ToList(), cancellationToken);
                store.AddAction("Remove torrent", item.Path, item.Size, $"qBittorrent: {names} (files kept)");
                notes.Add("torrent removed from qBittorrent");
            }

            var target = CleanPlan.Target(_options, item.Path, DateTime.Now, Mounts.Of(item.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (item.IsDirectory)
            {
                Directory.Move(item.Path, target);
            }
            else
            {
                File.Move(item.Path, target);
            }

            store.AddRecycled(new RecycledItem
            {
                Original = item.Path,
                Path = target,
                Size = item.Size,
                Recycled = DateTime.UtcNow,
                Verdict = report.Verdict.Label(),
                Note = string.Join("; ", notes)
            });
            var shown = Paths.IsUnder(target, _options.DownloadsRoot) ? Path.GetRelativePath(_options.DownloadsRoot, target) : target;
            store.AddAction("Recycle", item.Path, item.Size, $"{report.Verdict.Label()} → {shown}");
            logger.LogInformation("Recycled {Path} -> {Target}", item.Path, target);

            if (report.SabJobs.Count > 0)
            {
                try
                {
                    await sabnzbd.DeleteHistoryAsync(report.SabJobs.Select(j => j.NzoId).ToList(), cancellationToken);
                    store.AddAction("Remove history", item.Path, 0, $"SABnzbd: {string.Join(", ", report.SabJobs.Select(j => j.Name))}");
                    notes.Add("SABnzbd history entry removed");
                }
                catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    // The files are in the recycle folder either way; only the history line stays.
                    store.AddAction("Remove history", item.Path, 0, e.Message, ok: false);
                    notes.Add($"SABnzbd history entry kept ({e.Message})");
                }
            }

            return new CleanOutcome(item.Path, true, notes.Count == 0 ? "Recycled." : $"Recycled; {string.Join(", ", notes)}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            var message = Reason(e);
            store.AddAction(offer.Kind == CleanKind.RemoveTorrent && notes.Count == 0 ? "Remove torrent" : "Recycle", item.Path, item.Size, message, ok: false);
            logger.LogWarning("Clean up of {Path} failed: {Message}", item.Path, message);
            return new CleanOutcome(item.Path, false, notes.Count > 0 ? $"{string.Join(", ", notes)}, but: {message}" : message);
        }
    }

    /// <summary>Moves a recycled item back where it was.</summary>
    public string? Restore(long id)
    {
        if (!_options.AllowChanges)
        {
            return UnpackPlan.ChangesOff;
        }

        var item = store.Recycled().FirstOrDefault(r => r.Id == id);
        if (item == null)
        {
            return "It is not in the recycle folder.";
        }

        if (!Exists(item.Path))
        {
            store.RemoveRecycled(id);
            Changed?.Invoke();
            return "It is not in the recycle folder any more.";
        }

        if (Exists(item.Original))
        {
            var taken = $"Something is already at {item.Original}. Move it away first.";
            store.AddAction("Restore", item.Original, item.Size, taken, ok: false);
            return taken;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(item.Original)!);
            if (Directory.Exists(item.Path))
            {
                Directory.Move(item.Path, item.Original);
            }
            else
            {
                File.Move(item.Path, item.Original);
            }

            store.RemoveRecycled(id);
            store.AddAction("Restore", item.Original, item.Size, $"from {item.Path}");
            TidyParents(item.Path);
            logger.LogInformation("Restored {Path}", item.Original);
            scans.ScanNow();
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            store.AddAction("Restore", item.Original, item.Size, Reason(e), ok: false);
            return Reason(e);
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Deletes recycled items for good: the ones picked, or all of them.</summary>
    public (int Count, long Bytes, string? Error) Delete(IEnumerable<long>? ids, string why = "Delete")
    {
        if (!_options.AllowChanges)
        {
            return (0, 0, UnpackPlan.ChangesOff);
        }

        var all = store.Recycled();
        var picked = ids == null ? all : all.Where(r => ids.Contains(r.Id)).ToList();
        var count = 0;
        long bytes = 0;
        string? error = null;
        foreach (var item in picked)
        {
            try
            {
                // Only ever inside a recycle folder: a database row can't point Delete anywhere else.
                if (!CleanPlan.InRecycle(_options, item.Path))
                {
                    throw new IOException($"{item.Path} is not inside a recycle folder; left alone.");
                }

                if (Directory.Exists(item.Path))
                {
                    Directory.Delete(item.Path, recursive: true);
                }
                else if (File.Exists(item.Path))
                {
                    File.Delete(item.Path);
                }

                store.RemoveRecycled(item.Id);
                store.AddAction(why, item.Original, item.Size, $"deleted {item.Path}");
                TidyParents(item.Path);
                count++;
                bytes += item.Size;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                store.AddAction(why, item.Original, item.Size, Reason(e), ok: false);
                error = Reason(e);
            }
        }

        if (count > 0)
        {
            logger.LogInformation("{Why}: {Count} recycled items, {Size}", why, count, Format.Size(bytes));
        }

        Changed?.Invoke();
        return (count, bytes, error);
    }

    /// <summary>Deletes what has been in the recycle folder longer than RecycleDays.</summary>
    public (int Count, long Bytes) Purge(DateTime nowUtc)
    {
        if (!_options.AllowChanges)
        {
            return (0, 0);
        }

        var old = store.Recycled().Where(r => r.DeleteAt(_options.RecycleDays) <= nowUtc).Select(r => r.Id).ToList();
        if (old.Count == 0)
        {
            return (0, 0);
        }

        var (count, bytes, _) = Delete(old, "Purge");
        return (count, bytes);
    }

    /// <summary>Removes the empty dated folders a restore or delete leaves behind, up to the recycle folder.</summary>
    private void TidyParents(string path)
    {
        var name = Path.GetFileName(_options.RecycleFolder);
        for (var dir = Path.GetDirectoryName(path); dir != null && Path.GetFileName(dir) != name && CleanPlan.InRecycle(_options, dir);
             dir = Path.GetDirectoryName(dir))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
                else
                {
                    return;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string Reason(Exception e) => e.Message switch
    {
        var m when m.Contains("Read-only file system", StringComparison.OrdinalIgnoreCase) =>
            "/downloads is mounted read-only. Mount it read-write (without :ro) to clean up.",
        var m when m.Contains("cross-device", StringComparison.OrdinalIgnoreCase) || m.Contains("different volume", StringComparison.OrdinalIgnoreCase) =>
            "The recycle folder is on another mount, so the move would be a copy. Nothing was moved.",
        _ => e.Message
    };
}
