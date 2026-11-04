using Scratcharr.Options;
using Scratcharr.Scan;

namespace Scratcharr.Clean;

public enum CleanKind
{
    /// <summary>Nothing can be done from Scratcharr; the text says why.</summary>
    None,

    /// <summary>Move it to the recycle folder (and drop its SABnzbd history entry).</summary>
    Recycle,

    /// <summary>Remove the torrent in qBittorrent (files kept), then recycle the files.</summary>
    RemoveTorrent
}

/// <summary>What Clean up can do with an item, in words for the page.</summary>
public sealed record CleanOffer(CleanKind Kind, string Text)
{
    public bool Allowed => Kind != CleanKind.None;

    public string Button => Kind == CleanKind.RemoveTorrent ? "Remove torrent and recycle" : "Recycle";
}

/// <summary>The rules for Clean up. Pure: the same checks run on the page and again right before acting, on a fresh scan.</summary>
public static class CleanPlan
{
    public static CleanOffer Offer(ItemReport report, ScratcharrOptions options)
    {
        var item = report.Item;
        if (item.Protected || report.Verdict == Verdict.Protected)
        {
            return No("Protected: another app's working folder, or Scratcharr's own.");
        }

        if (item.Kind == FolderKind.UsenetIncomplete)
        {
            return No("SABnzbd's working folder: SABnzbd cleans it up itself.");
        }

        if (report.Verdict == Verdict.Unknown)
        {
            return No("A client could not be read, so Scratcharr won't guess who owns it.");
        }

        if (report.Queue.FirstOrDefault() is { } queued)
        {
            return No($"{queued.App} is waiting to import it. Remove it from {queued.App}'s queue instead.");
        }

        if (report.SabJobs.Any(j => j.InQueue))
        {
            return No("SABnzbd is still working on it.");
        }

        var days = $"Kept {options.RecycleDays:0.#} days in the recycle folder, then deleted for good.";
        if (report.Torrents.Count > 0)
        {
            // qBittorrent names unfinished files name.!qB while content_path keeps the real name.
            var path = DiskScanner.IsPart(item.Path) ? item.Path[..^4] : item.Path;
            if (report.Torrents.Any(t => t.ContentPath != path))
            {
                return No("The torrent holds more than this item. Remove it in qBittorrent.");
            }

            var torrent = report.Torrents.Count == 1 ? "the torrent" : $"the {report.Torrents.Count} torrents";
            var unfinished = report.Torrents.Any(t => !t.Finished) ? " It is not finished downloading." : "";
            return new CleanOffer(CleanKind.RemoveTorrent,
                $"Removes {torrent} from qBittorrent (without its files), then moves the files to the recycle folder. {days}{unfinished}");
        }

        var sab = report.SabJobs.Count > 0 ? ", and removes its SABnzbd history entry" : "";
        return new CleanOffer(CleanKind.Recycle, $"Moves it to the recycle folder{sab}. {days}");
    }

    /// <summary>
    /// Where Recycle puts an item: &lt;recycle&gt;/&lt;date&gt;/&lt;path inside /downloads&gt;. An item on another mount
    /// inside /downloads (torrents and usenet as separate datasets) uses a .recycle folder at the top of that mount,
    /// so the move is a rename and never a copy.
    /// </summary>
    public static string Target(ScratcharrOptions options, string path, DateTime nowLocal, string? mount)
    {
        var root = mount != null && Paths.IsUnder(mount, options.DownloadsRoot)
            ? System.IO.Path.Combine(mount, System.IO.Path.GetFileName(options.RecycleFolder))
            : options.RecycleFolder;
        var relative = Paths.IsUnder(path, options.DownloadsRoot)
            ? System.IO.Path.GetRelativePath(options.DownloadsRoot, path)
            : System.IO.Path.GetFileName(path);
        var target = System.IO.Path.Combine(root, nowLocal.ToString("yyyy-MM-dd"), relative);
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
        {
            target = System.IO.Path.Combine(root, nowLocal.ToString("yyyy-MM-dd"), $"{relative} ({n})");
        }

        return target;
    }

    /// <summary>The recycle folders: the main one and the one on each mount inside /downloads.</summary>
    public static bool InRecycle(ScratcharrOptions options, string path) =>
        Paths.IsUnder(path, options.RecycleFolder)
        || path.Split('/').Contains(System.IO.Path.GetFileName(options.RecycleFolder)) && Paths.IsUnder(path, options.DownloadsRoot);

    private static CleanOffer No(string why) => new(CleanKind.None, why);
}
