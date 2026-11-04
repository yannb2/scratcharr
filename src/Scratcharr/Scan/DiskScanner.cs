using Scratcharr.Options;

namespace Scratcharr.Scan;

/// <summary>One entry of a watched folder: a torrent's folder or file, a usenet job, something copied by hand.</summary>
public sealed record DiskItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }

    /// <summary>The watched folder's name ("Torrents"), or "Downloads" for loose entries at the top.</summary>
    public required string Folder { get; init; }

    public FolderKind Kind { get; init; }

    /// <summary>The category folder it sits in (radarr, manual...), or empty.</summary>
    public string Category { get; init; } = "";

    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public int Files { get; init; }

    /// <summary>The newest file time inside (for a folder) or the file's own time.</summary>
    public DateTime Modified { get; init; }

    /// <summary>qBittorrent's unfinished files (*.!qB).</summary>
    public int PartFiles { get; init; }

    /// <summary>Archive sets: the first part of a .rar set, .7z and .zip files.</summary>
    public int Archives { get; init; }

    /// <summary>.iso files, plus 1 for each BDMV folder.</summary>
    public int Discs { get; init; }

    public int Videos { get; init; }

    /// <summary>The 20 largest files, for the item page.</summary>
    public IReadOnlyList<DiskFile> LargestFiles { get; init; } = [];

    /// <summary>Under a protected folder (Ripparr's work and done folders, the recycle bin).</summary>
    public bool Protected { get; init; }

    public string? ReadError { get; init; }
}

public sealed record DiskFile(string RelativePath, long Size, DateTime Modified);

/// <summary>Walks the watched folders and measures each entry. Reads only metadata, never file contents.</summary>
public static class DiskScanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".ts", ".m2ts", ".mov", ".wmv", ".mpg", ".mpeg", ".webm"
    };

    /// <param name="categories">Folder names that hold one item per entry (radarr, sonarr, manual, the clients' categories).</param>
    /// <param name="protectedFolders">The protected folders in force (Settings page or environment); null = the options' own.</param>
    public static List<DiskItem> Scan(ScratcharrOptions options, IReadOnlySet<string> categories, CancellationToken cancellationToken,
        IReadOnlyList<string>? protectedFolders = null)
    {
        var shield = protectedFolders ?? options.Protected;
        var items = new List<DiskItem>();
        var watched = options.Folders.Select(f => f.Path).ToList();
        foreach (var folder in options.Folders)
        {
            if (!Directory.Exists(folder.Path))
            {
                continue;
            }

            foreach (var entry in Entries(folder.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A watched folder inside another one (usenet/incomplete under usenet) is listed on its own.
                if (watched.Contains(entry.FullName))
                {
                    continue;
                }

                var opens = folder.Kind != FolderKind.Manual && entry is DirectoryInfo && categories.Contains(entry.Name);
                if (opens)
                {
                    foreach (var inner in Entries(entry.FullName))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        items.Add(Measure(inner, folder.Name, folder.Kind, entry.Name, options, shield));
                    }
                }
                else
                {
                    items.Add(Measure(entry, folder.Name, folder.Kind, "", options, shield));
                }
            }
        }

        // Loose entries at the top of /downloads that no watched folder covers (an ISO dropped there,
        // Ripparr's folders). Parents of watched folders (usenet) are not items themselves.
        if (Directory.Exists(options.DownloadsRoot))
        {
            foreach (var entry in Entries(options.DownloadsRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (watched.Any(w => Paths.Related(entry.FullName, w)))
                {
                    continue;
                }

                items.Add(Measure(entry, "Downloads", FolderKind.Manual, "", options, shield));
            }
        }

        return items;
    }

    private static IEnumerable<FileSystemInfo> Entries(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                RecurseSubdirectories = false
            }).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static DiskItem Measure(FileSystemInfo entry, string folder, FolderKind kind, string category, ScratcharrOptions options,
        IReadOnlyList<string> protectedFolders)
    {
        var path = Paths.Clean(entry.FullName);
        // A folder 7-Zip is still filling (Unpack), or a copy from USB, is Scratcharr's own work in progress.
        var isProtected = protectedFolders.Any(p => Paths.IsUnder(path, p) || path == p)
                          || entry.Name.EndsWith(Jobs.UnpackPlan.PartialSuffix, StringComparison.Ordinal)
                          || entry.Name.EndsWith(Usb.CopyPlan.PartialSuffix, StringComparison.Ordinal)
                          || entry.Name == System.IO.Path.GetFileName(options.RecycleFolder);
        if (entry is FileInfo file)
        {
            return new DiskItem
            {
                Path = path, Name = entry.Name, Folder = folder, Kind = kind, Category = category,
                Size = Safe(() => file.Length), Files = 1, Modified = entry.LastWriteTimeUtc,
                PartFiles = IsPart(entry.Name) ? 1 : 0,
                Archives = IsArchiveStart(entry.Name) ? 1 : 0,
                Discs = entry.Name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                Videos = IsVideo(entry.Name) ? 1 : 0,
                LargestFiles = [new DiskFile(entry.Name, Safe(() => file.Length), entry.LastWriteTimeUtc)],
                Protected = isProtected
            };
        }

        long size = 0;
        int files = 0, parts = 0, archives = 0, discs = 0, videos = 0;
        // A folder's own time changes whenever something is added or removed inside; the files' times say more.
        var modified = DateTime.MinValue;
        var largest = new List<DiskFile>();
        string? error = null;
        try
        {
            var options2 = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var inner in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options2))
            {
                if (inner is DirectoryInfo directory)
                {
                    if (directory.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase))
                    {
                        discs++;
                    }

                    continue;
                }

                var f = (FileInfo)inner;
                var length = Safe(() => f.Length);
                size += length;
                files++;
                if (f.LastWriteTimeUtc > modified)
                {
                    modified = f.LastWriteTimeUtc;
                }

                parts += IsPart(f.Name) ? 1 : 0;
                archives += IsArchiveStart(f.Name) ? 1 : 0;
                discs += f.Name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                videos += IsVideo(f.Name) ? 1 : 0;
                largest.Add(new DiskFile(System.IO.Path.GetRelativePath(path, f.FullName), length, f.LastWriteTimeUtc));
                if (largest.Count > 200)
                {
                    largest = largest.OrderByDescending(l => l.Size).Take(20).ToList();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
        }

        if (modified == DateTime.MinValue)
        {
            modified = entry.LastWriteTimeUtc;
        }

        return new DiskItem
        {
            Path = path, Name = entry.Name, Folder = folder, Kind = kind, Category = category, IsDirectory = true,
            Size = size, Files = files, Modified = modified, PartFiles = parts, Archives = archives, Discs = discs, Videos = videos,
            LargestFiles = largest.OrderByDescending(l => l.Size).Take(20).ToList(),
            Protected = isProtected, ReadError = error
        };
    }

    public static bool IsPart(string name) => name.EndsWith(".!qB", StringComparison.OrdinalIgnoreCase);

    public static bool IsVideo(string name) => VideoExtensions.Contains(System.IO.Path.GetExtension(name));

    /// <summary>
    /// The file that starts an archive set: name.rar (old style, with .r00 parts), name.part1.rar /
    /// name.part01.rar (new style), name.7z, name.zip, name.7z.001. Later parts are not counted again.
    /// </summary>
    public static bool IsArchiveStart(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".rar"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(lower, @"\.part(\d+)\.rar$");
            return !m.Success || int.Parse(m.Groups[1].Value) == 1;
        }

        return lower.EndsWith(".7z") || lower.EndsWith(".zip") || lower.EndsWith(".7z.001");
    }

    private static long Safe(Func<long> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>Path comparisons, always on clean absolute paths (no trailing slash).</summary>
public static class Paths
{
    public static string Clean(string path) => path.Length > 1 ? path.TrimEnd('/') : path;

    /// <summary>True when path is inside parent (not equal).</summary>
    public static bool IsUnder(string path, string parent)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(parent))
        {
            return false;
        }

        var p = Clean(parent);
        return path.Length > p.Length && path.StartsWith(p, StringComparison.Ordinal) && (p == "/" || path[p.Length] == '/');
    }

    /// <summary>Equal, or one inside the other.</summary>
    public static bool Related(string a, string b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && (Clean(a) == Clean(b) || IsUnder(a, b) || IsUnder(b, a));
}
