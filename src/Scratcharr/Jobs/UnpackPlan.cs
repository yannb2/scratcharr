using Scratcharr.Options;
using Scratcharr.Scan;
using Scratcharr.Unpack;

namespace Scratcharr.Jobs;

/// <summary>The checks before an unpack is queued, and again right before it runs.</summary>
public static class UnpackPlan
{
    /// <summary>Space kept free on top of the unpacked size.</summary>
    public const long Headroom = 1L << 30;

    public const string PartialSuffix = ".unpacking";

    public const string ChangesOff =
        "Changes are off: set Scratcharr__AllowChanges=true and mount /downloads read-write (without :ro) to unpack.";

    /// <summary>
    /// Items the Unpack page lists: those holding archives, except protected folders and SABnzbd's incomplete
    /// folder (SABnzbd unpacks its own jobs).
    /// </summary>
    public static bool Offers(DiskItem item) => item.Archives > 0 && !item.Protected && item.Kind != FolderKind.UsenetIncomplete;

    /// <summary>The target folder for a name typed on the page, or null when the name can't be used.</summary>
    public static string? Target(ScratcharrOptions options, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name is "." or ".." || name.StartsWith('.') || name.IndexOfAny(['/', '\\', '\0']) >= 0
            || name.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.Combine(options.DiscsFolder, name);
    }

    /// <summary>Null when the unpack can go ahead, else the reason it can't.</summary>
    public static string? Problem(ScratcharrOptions options, string start, string? target, long unpackedSize, long freeBytes,
        IEnumerable<Job> jobs)
    {
        if (!options.AllowChanges)
        {
            return ChangesOff;
        }

        if (target == null)
        {
            return "Pick a folder name without slashes, not starting with a dot.";
        }

        if (!Paths.IsUnder(target, options.DiscsFolder))
        {
            return $"The target has to be inside {options.DiscsFolder}.";
        }

        if (!File.Exists(start))
        {
            return "The archive is not there any more.";
        }

        if (!Paths.IsUnder(start, options.DownloadsRoot))
        {
            return $"Only archives under {options.DownloadsRoot} are unpacked.";
        }

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() || File.Exists(target))
        {
            return $"{target} already exists. Pick another name.";
        }

        if (jobs.Any(j => j.Open && j.Target == target))
        {
            return "An unpack into that folder is already queued.";
        }

        if (freeBytes > 0 && unpackedSize + Headroom > freeBytes)
        {
            return $"Not enough space: it needs {Format.Size(unpackedSize)} and {Format.Size(freeBytes)} is free.";
        }

        return null;
    }

    /// <summary>
    /// What Ripparr can open in a finished folder: ISO files and folders holding BDMV, a few levels deep.
    /// </summary>
    public static IReadOnlyList<string> Discs(string folder, int depth = 4)
    {
        // A copied ISO file is itself the disc.
        if (File.Exists(folder))
        {
            return folder.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? [folder] : [];
        }

        var found = new List<string>();
        Walk(folder, depth, found);
        return found;
    }

    private static void Walk(string folder, int depth, List<string> found)
    {
        try
        {
            if (Directory.GetDirectories(folder).Any(d => Path.GetFileName(d).Equals("BDMV", StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(folder);
                return;
            }

            found.AddRange(Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase));
            if (depth > 0)
            {
                foreach (var sub in Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
                {
                    Walk(sub, depth - 1, found);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable: nothing to offer.
        }
    }

    /// <summary>Ripparr's disc page for a path, or null when no Ripparr address is set.</summary>
    public static string? RipparrLink(ScratcharrOptions options, string disc) =>
        string.IsNullOrWhiteSpace(options.RipparrUrl)
            ? null
            : $"{options.RipparrUrl.TrimEnd('/')}/disc?path={Uri.EscapeDataString(disc)}";

    /// <summary>The folder an unpack goes to unless another name is typed: /downloads/discs/&lt;set name&gt;.</summary>
    public static string DefaultTarget(ScratcharrOptions options, ArchiveSet set) => Path.Combine(options.DiscsFolder, set.Name);
}
