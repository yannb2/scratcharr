using Scratcharr.Clients;
using Scratcharr.Options;

namespace Scratcharr.Scan;

public enum Verdict
{
    /// <summary>A client still owns it: downloading, seeding, queued.</summary>
    Active,

    /// <summary>An arr app is about to import it.</summary>
    WaitingForImport,

    /// <summary>Stopped at its seeding limit, still listed in qBittorrent.</summary>
    SeedingDone,

    /// <summary>An arr app imported it (a copy) and no client owns it any more.</summary>
    ImportedLeftover,

    /// <summary>The download or the import failed.</summary>
    Failed,

    /// <summary>A manual download or a folder filled by hand: kept until you decide.</summary>
    Manual,

    /// <summary>Unfinished torrent files that no torrent owns.</summary>
    HalfFinished,

    /// <summary>Nothing claims it at all.</summary>
    Stray,

    /// <summary>The client that would know could not be read.</summary>
    Unknown,

    /// <summary>Another app's working folder: never touched.</summary>
    Protected
}

public static class VerdictText
{
    public static string Label(this Verdict verdict) => verdict switch
    {
        Verdict.Active => "Active",
        Verdict.WaitingForImport => "Waiting for import",
        Verdict.SeedingDone => "Seeding done",
        Verdict.ImportedLeftover => "Imported, leftover",
        Verdict.Failed => "Failed",
        Verdict.Manual => "Manual",
        Verdict.HalfFinished => "Half-finished",
        Verdict.Stray => "Stray",
        Verdict.Unknown => "Can't tell",
        Verdict.Protected => "Protected",
        _ => verdict.ToString()
    };

    /// <summary>The Bootstrap colour of the badge.</summary>
    public static string Css(this Verdict verdict) => verdict switch
    {
        Verdict.Active => "success",
        Verdict.WaitingForImport => "info",
        Verdict.SeedingDone => "primary",
        Verdict.ImportedLeftover => "warning",
        Verdict.Failed => "danger",
        Verdict.Manual => "secondary",
        Verdict.HalfFinished => "warning",
        Verdict.Stray => "danger",
        Verdict.Unknown => "secondary",
        Verdict.Protected => "dark",
        _ => "secondary"
    };

    public static string Help(this Verdict verdict) => verdict switch
    {
        Verdict.Active => "A client still owns it: downloading, seeding or queued. Keep.",
        Verdict.WaitingForImport => "An arr app has it in its queue and will import it. Keep.",
        Verdict.SeedingDone => "Stopped at its seeding limit and still listed in qBittorrent.",
        Verdict.ImportedLeftover => "Imported (as a copy) and no client owns it any more: safe to clean.",
        Verdict.Failed => "The download or the import failed. Review.",
        Verdict.Manual => "A manual download or a folder filled by hand. Kept until you decide.",
        Verdict.HalfFinished => "Unfinished torrent files that no torrent owns: safe to clean.",
        Verdict.Stray => "Nothing claims it: no torrent, no SABnzbd job, nothing in the arr apps. Review.",
        Verdict.Unknown => "The client that would know could not be read, so nothing is assumed.",
        Verdict.Protected => "Another app's working folder (Ripparr, the recycle bin). Never touched.",
        _ => ""
    };

    /// <summary>Verdicts worth a look: they take space nobody is going to free.</summary>
    public static bool NeedsReview(this Verdict verdict) =>
        verdict is Verdict.Stray or Verdict.ImportedLeftover or Verdict.Failed or Verdict.HalfFinished;
}

/// <summary>Who removes an item from the disk, and when.</summary>
public sealed record Removal
{
    public required string Text { get; init; }
    public DateTime? Due { get; init; }

    /// <summary>Nothing will ever remove it on its own.</summary>
    public bool Nobody { get; init; }

    public bool Overdue(DateTime nowUtc) => Due is { } due && due < nowUtc.AddHours(-1);
}

public sealed record ArrMatch<T>(ArrApp App, T Item);

/// <summary>An item with everything that was found about it.</summary>
public sealed record ItemReport
{
    public required DiskItem Item { get; init; }
    public Verdict Verdict { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyList<Torrent> Torrents { get; init; } = [];
    public IReadOnlyList<SabJob> SabJobs { get; init; } = [];
    public IReadOnlyList<ArrMatch<ArrQueueItem>> Queue { get; init; } = [];
    public IReadOnlyList<ArrMatch<ArrHistoryEvent>> History { get; init; } = [];
    public Removal? Removal { get; init; }
    public DateTime FirstSeen { get; init; }

    /// <summary>A manual download (the manual or prowlarr category, or a folder filled by hand): kept until you decide.</summary>
    public bool IsManual => Verdict == Verdict.Manual || ScratcharrOptions.IsManualCategory(Item.Category);

    /// <summary>Nothing will ever remove it, and it is not a manual item you keep on purpose.</summary>
    public bool StaysForever => Removal?.Nobody == true && !IsManual;

    /// <summary>A stable short id for links: a hash of the path.</summary>
    public string Id => Ids.For(Item.Path);
}

public static class Ids
{
    public static string For(string path)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

/// <summary>What the clients said, gathered for one scan.</summary>
public sealed record Evidence
{
    public Reading<QbittorrentState> Qbittorrent { get; init; } = Reading<QbittorrentState>.NotConfigured();
    public Reading<SabnzbdState> Sabnzbd { get; init; } = Reading<SabnzbdState>.NotConfigured();
    public IReadOnlyDictionary<ArrApp, Reading<ArrState>> Arr { get; init; } = new Dictionary<ArrApp, Reading<ArrState>>();

    public IEnumerable<ArrState> ArrStates => Arr.Values.Where(r => r.Ok).Select(r => r.State!);
}

/// <summary>Decides each item's verdict from the evidence. Pure: no I/O, so the rules are tested on their own.</summary>
public static class Classifier
{
    public static ItemReport Classify(DiskItem item, Evidence evidence, DateTime nowUtc, DateTime firstSeen)
    {
        var torrents = evidence.Qbittorrent.State?.Torrents.Where(t => Claims(t, item)).ToList() ?? [];
        var sab = evidence.Sabnzbd.State;
        var jobs = sab == null ? [] : sab.Queue.Concat(sab.History).Where(j => Claims(j, item)).ToList();
        var ids = torrents.Select(t => t.Hash).Concat(jobs.Select(j => j.NzoId.ToUpperInvariant()))
            .Where(i => i.Length > 0).ToHashSet(StringComparer.Ordinal);

        var queue = new List<ArrMatch<ArrQueueItem>>();
        var history = new List<ArrMatch<ArrHistoryEvent>>();
        foreach (var arr in evidence.ArrStates)
        {
            queue.AddRange(arr.Queue
                .Where(q => (q.DownloadId.Length > 0 && ids.Contains(q.DownloadId)) || Paths.Related(q.OutputPath, item.Path))
                .Select(q => new ArrMatch<ArrQueueItem>(arr.App, q)));
            history.AddRange(arr.History
                .Where(h => (h.DownloadId.Length > 0 && ids.Contains(h.DownloadId))
                            || (h.DroppedPath.Length > 0 && (h.DroppedPath == item.Path || Paths.IsUnder(h.DroppedPath, item.Path))))
                .Select(h => new ArrMatch<ArrHistoryEvent>(arr.App, h)));
        }

        history = history.OrderByDescending(h => h.Item.Date).ToList();
        var (verdict, reasons) = Decide(item, evidence, torrents, jobs, queue, history);
        return new ItemReport
        {
            Item = item,
            Verdict = verdict,
            Reasons = reasons,
            Torrents = torrents,
            SabJobs = jobs,
            Queue = queue,
            History = history,
            Removal = RemovalFor(item, evidence, torrents, jobs, verdict, nowUtc),
            FirstSeen = firstSeen
        };
    }

    /// <summary>A torrent owns the item when its content path is the item, inside it, or holds it.</summary>
    public static bool Claims(Torrent torrent, DiskItem item)
    {
        if (torrent.ContentPath.Length == 0)
        {
            return false;
        }

        // qBittorrent names unfinished files name.!qB while content_path keeps the real name.
        var path = DiskScanner.IsPart(item.Path) ? item.Path[..^4] : item.Path;
        // A torrent whose content path is a whole category folder (no subfolder layout) can't be matched to one item.
        if (torrent.ContentPath == torrent.SavePath && Paths.IsUnder(path, torrent.ContentPath))
        {
            return false;
        }

        return Paths.Related(torrent.ContentPath, path);
    }

    public static bool Claims(SabJob job, DiskItem item) =>
        Paths.Related(job.Path, item.Path) || Paths.Related(job.IncompletePath, item.Path);

    private static (Verdict, List<string>) Decide(DiskItem item, Evidence evidence, List<Torrent> torrents, List<SabJob> jobs,
        List<ArrMatch<ArrQueueItem>> queue, List<ArrMatch<ArrHistoryEvent>> history)
    {
        var reasons = new List<string>();
        if (item.Protected)
        {
            reasons.Add("Inside a protected folder.");
            return (Verdict.Protected, reasons);
        }

        var imported = history.FirstOrDefault(h => h.Item.Kind == ArrEventKind.Imported);
        var failed = history.FirstOrDefault(h => h.Item.Kind == ArrEventKind.Failed);
        var pending = queue.FirstOrDefault(q => q.Item.TrackedState is "importPending" or "importing" or "downloading" or "" );
        var queueFailed = queue.FirstOrDefault(q => q.Item.TrackedState is "failed" or "failedPending" or "importBlocked");

        if (torrents.Count > 0)
        {
            var torrent = torrents[0];
            reasons.Add($"qBittorrent: \"{torrent.Name}\" is {torrent.StateWord.ToLowerInvariant()}" +
                        (torrent.Category.Length > 0 ? $" (category {torrent.Category})." : " (no category)."));
            if (imported != null)
            {
                reasons.Add($"{imported.App} imported it {Format.TimeAgo(imported.Item.Date)}.");
            }

            if (torrent.Errored)
            {
                reasons.Add("qBittorrent reports an error for this torrent.");
                return (Verdict.Active, reasons);
            }

            if (!torrent.Finished)
            {
                return (Verdict.Active, reasons);
            }

            if (queueFailed != null)
            {
                reasons.Add($"{queueFailed.App} could not import it: {string.Join(" ", queueFailed.Item.Messages.Take(2))}".TrimEnd() );
                return (Verdict.Failed, reasons);
            }

            if (pending != null && imported == null)
            {
                reasons.Add($"{pending.App} has it in its queue ({Words(pending.Item.TrackedState)}).");
                return (Verdict.WaitingForImport, reasons);
            }

            return torrent.Stopped ? (Verdict.SeedingDone, reasons) : (Verdict.Active, reasons);
        }

        var queued = jobs.FirstOrDefault(j => j.InQueue);
        if (queued != null)
        {
            reasons.Add($"SABnzbd: \"{queued.Name}\" is in the queue ({queued.Status.ToLowerInvariant()}).");
            return (Verdict.Active, reasons);
        }

        var job = jobs.FirstOrDefault();
        if (job != null)
        {
            reasons.Add($"SABnzbd history: \"{job.Name}\" {job.Status.ToLowerInvariant()}" +
                        (job.Completed is { } done ? $" {Format.TimeAgo(done)}" : "") +
                        (job.Category.Length > 0 ? $" (category {job.Category})." : "."));
            if (job.Failed)
            {
                if (job.FailMessage.Length > 0)
                {
                    reasons.Add(job.FailMessage);
                }

                return (Verdict.Failed, reasons);
            }
        }

        if (pending != null && imported == null)
        {
            reasons.Add($"{pending.App} has it in its queue ({Words(pending.Item.TrackedState)}).");
            return (Verdict.WaitingForImport, reasons);
        }

        if (imported != null)
        {
            reasons.Add($"{imported.App} imported it {Format.TimeAgo(imported.Item.Date)}" +
                        (imported.Item.ImportedPath.Length > 0 ? $" to {imported.Item.ImportedPath}." : "."));
            if (torrents.Count == 0 && job == null)
            {
                reasons.Add(item.Kind == FolderKind.Usenet ? "No longer in SABnzbd's queue." : "No torrent owns it any more.");
            }

            return (Verdict.ImportedLeftover, reasons);
        }

        if (queueFailed != null || failed != null)
        {
            reasons.Add(queueFailed != null
                ? $"{queueFailed.App} could not import it: {string.Join(" ", queueFailed.Item.Messages.Take(2))}".TrimEnd()
                : $"{failed!.App}: {Words(failed.Item.EventType)} {Format.TimeAgo(failed.Item.Date)}. {failed.Item.Message}".TrimEnd());
            return (Verdict.Failed, reasons);
        }

        if (item.PartFiles > 0)
        {
            reasons.Add($"{item.PartFiles} unfinished torrent file(s) (.!qB) and no torrent owns them.");
            return (Verdict.HalfFinished, reasons);
        }

        // Nothing claims it: say why that can be trusted, or why not.
        var needs = item.Kind switch
        {
            FolderKind.Torrents => evidence.Qbittorrent.Ok ? null : "qBittorrent",
            FolderKind.Usenet or FolderKind.UsenetIncomplete => evidence.Sabnzbd.Ok ? null : "SABnzbd",
            _ => null
        };

        if (ScratcharrOptions.IsManualCategory(item.Category) || item.Kind == FolderKind.Manual)
        {
            reasons.Add(item.Kind == FolderKind.Manual
                ? $"In {item.Folder}, a folder filled by hand."
                : item.Category.Equals("prowlarr", StringComparison.OrdinalIgnoreCase)
                    ? "In the prowlarr category folder: grabbed by hand from a Prowlarr search."
                    : "In the manual category folder.");
            if (job != null)
            {
                reasons.Add("Finished in SABnzbd; no arr app imports it.");
            }

            return (Verdict.Manual, reasons);
        }

        if (needs != null)
        {
            reasons.Add($"{needs} could not be read, so it is not called a stray.");
            return (Verdict.Unknown, reasons);
        }

        if (job != null)
        {
            reasons.Add("Finished in SABnzbd, but no arr app has it in its queue or history.");
            return (Verdict.Stray, reasons);
        }

        reasons.Add(item.Kind switch
        {
            FolderKind.Torrents => "No torrent in qBittorrent owns it.",
            FolderKind.UsenetIncomplete => "Left in SABnzbd's temporary folder with no job.",
            FolderKind.Usenet => "No SABnzbd job owns it.",
            _ => "Nothing claims it."
        });
        var missing = evidence.Arr.Where(a => a.Value.Configured && !a.Value.Ok).Select(a => a.Key.ToString()).ToList();
        reasons.Add(missing.Count > 0
            ? $"No arr app knows it ({string.Join(", ", missing)} could not be read)."
            : "No arr app has it in its queue or recent history.");
        return (Verdict.Stray, reasons);
    }

    /// <summary>Who will take it off the disk, and when, under the current settings.</summary>
    internal static Removal? RemovalFor(DiskItem item, Evidence evidence, List<Torrent> torrents, List<SabJob> jobs, Verdict verdict, DateTime nowUtc)
    {
        if (verdict == Verdict.Protected)
        {
            return null;
        }

        if (torrents.Count > 0 && evidence.Qbittorrent.State is { } qbt)
        {
            var torrent = torrents[0];
            var limitMinutes = torrent.LimitMinutes(qbt.Limits);
            var who = WhoAfterTheLimit(torrent.Category, qbt.Limits.Action, evidence, "QBittorrent");
            if (limitMinutes == null && (torrent.RatioLimit == -1 || (torrent.RatioLimit == -2 && qbt.Limits.Ratio == null)))
            {
                return new Removal { Text = "No seeding limit: it seeds until someone removes it.", Nobody = true };
            }

            if (!torrent.Finished)
            {
                return new Removal { Text = $"Still downloading. Then: {who.Text}", Nobody = who.Nobody };
            }

            DateTime? due = null;
            if (limitMinutes is { } minutes)
            {
                var left = TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(torrent.SeedingSeconds);
                due = torrent.Stopped ? null : nowUtc + (left < TimeSpan.Zero ? TimeSpan.Zero : left);
            }

            var limitText = limitMinutes is { } l ? $"seeding limit {Hours(l)}" : "ratio limit";
            if (torrent.Stopped)
            {
                return new Removal { Text = $"Reached its {limitText}. {who.Text}", Nobody = who.Nobody, Due = who.Nobody ? null : nowUtc };
            }

            return new Removal
            {
                Text = due is { } d ? $"{limitText}, stops {Format.TimeUntil(d, nowUtc)}. {who.Text}" : $"{limitText}. {who.Text}",
                Due = due,
                Nobody = who.Nobody
            };
        }

        var job = jobs.FirstOrDefault(j => !j.InQueue);
        if (job != null && !job.Failed)
        {
            var client = ArrClientFor(evidence, "Sabnzbd", job.Category);
            return client is { } c && c.Client.RemoveCompleted
                ? new Removal { Text = $"{c.App} removes it after the import (Remove Completed is on)." }
                : client is { } off
                    ? new Removal { Text = $"{off.App} has Remove Completed off for SABnzbd: it stays.", Nobody = true }
                    : new Removal { Text = "No arr app uses this SABnzbd category: it stays.", Nobody = true };
        }

        if (job is { Failed: true })
        {
            var client = ArrClientFor(evidence, "Sabnzbd", job.Category);
            return client is { } c && c.Client.RemoveFailed
                ? new Removal { Text = $"{c.App} removes failed downloads (Remove Failed is on)." }
                : new Removal { Text = "Nothing removes failed jobs for this category: it stays.", Nobody = true };
        }

        return verdict is Verdict.Active or Verdict.WaitingForImport or Verdict.Unknown
            ? null
            : new Removal { Text = "Nothing will remove it: it stays until you clean it.", Nobody = true };
    }

    private static (string Text, bool Nobody) WhoAfterTheLimit(string category, ShareLimitAction action, Evidence evidence, string implementation)
    {
        switch (action)
        {
            case ShareLimitAction.RemoveWithFiles:
                return ("Then qBittorrent removes it with its files.", false);
            case ShareLimitAction.Remove:
                return ("Then qBittorrent removes the torrent but leaves its files here.", true);
            case ShareLimitAction.SuperSeeding:
                return ("Then qBittorrent keeps super-seeding it.", true);
        }

        var client = ArrClientFor(evidence, implementation, category);
        if (client is { } c)
        {
            return c.Client.RemoveCompleted
                ? ($"Then {c.App} removes it with its files (Remove Completed is on).", false)
                : ($"Then it stays: {c.App} has Remove Completed off.", true);
        }

        return (category.Length == 0
            ? "Then it stays: no category, so no arr app removes it."
            : $"Then it stays: no arr app uses the {category} category.", true);
    }

    /// <summary>The arr app whose download client of this kind uses this category.</summary>
    public static (ArrApp App, ArrDownloadClient Client)? ArrClientFor(Evidence evidence, string implementation, string category)
    {
        foreach (var arr in evidence.ArrStates)
        {
            var client = arr.Clients.FirstOrDefault(c => c.Enabled
                                                         && c.Implementation.Equals(implementation, StringComparison.OrdinalIgnoreCase)
                                                         && c.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
            if (client != null)
            {
                return (arr.App, client);
            }
        }

        return null;
    }

    public static string Hours(long minutes) =>
        minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";

    private static string Words(string camel) =>
        System.Text.RegularExpressions.Regex.Replace(camel, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant();
}
