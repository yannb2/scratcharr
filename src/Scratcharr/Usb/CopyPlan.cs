using Scratcharr.Jobs;
using Scratcharr.Options;
using Scratcharr.Scan;

namespace Scratcharr.Usb;

/// <summary>Where a copy from USB goes: Auto picks discs for ISOs and BDMV folders, import for the rest.</summary>
public enum CopyDestination
{
    Auto,
    Import,
    Discs
}

/// <summary>What a file or folder on a USB disk holds, so Auto can pick where it goes.</summary>
public enum UsbKind
{
    Disc,
    Video,
    Folder,
    File
}

/// <summary>
/// A file or folder on an external disk or share, with its size (folders: everything inside). Unreadable says why
/// all or part of it can't be read; such an entry is listed but can't be copied. Partial: only part of the folder
/// can't be read, so it can still be opened.
/// </summary>
public sealed record UsbEntry(string Path, string Name, bool IsDirectory, long Size, int Files, UsbKind Kind, DateTime Modified,
    string? Unreadable = null, bool Partial = false, int Archives = 0);

/// <summary>A USB disk: one folder under the USB root, with the free and total space of the mount it sits on.</summary>
public sealed record UsbDisk(string Path, string Name, long Free, long Total, bool Mounted);

/// <summary>The checks before a copy from USB is queued, and again right before it runs.</summary>
public static class CopyPlan
{
    public const string PartialSuffix = ".copying";

    /// <summary>The disks and shares under the USB root (/mnt/pc/usb01...), or none when the root is missing.</summary>
    public static IReadOnlyList<UsbDisk> Disks(ScratcharrOptions options)
    {
        if (!Directory.Exists(options.UsbRoot))
        {
            return [];
        }

        var rootDevice = Device(options.UsbRoot);
        return Directory.GetDirectories(options.UsbRoot)
            .Where(d => !IsLink(d))
            .Where(d => System.IO.Path.GetFileName(d) is var name && !name.StartsWith('.')
                        && !options.UsbSkip.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(d =>
            {
                long free = 0, total = 0;
                try
                {
                    var drive = new DriveInfo(d);
                    free = drive.AvailableFreeSpace;
                    total = drive.TotalSize;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // Unknown: shown without a size.
                }

                // An empty mount point (the disk is unplugged) sits on the same mount as the USB root.
                bool mounted;
                try
                {
                    mounted = rootDevice == null || Device(d) != rootDevice || Directory.EnumerateFileSystemEntries(d).Any();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    mounted = true; // There is something there, it just can't be listed: the folder page says why.
                }

                return new UsbDisk(d, System.IO.Path.GetFileName(d), free, total, mounted);
            })
            .ToList();
    }

    /// <summary>
    /// The files and folders in a folder of an external disk, folders first, each measured. An entry that can't be
    /// read, or only in part (Windows' System Volume Information...), is still listed, with the reason.
    /// </summary>
    public static IReadOnlyList<UsbEntry> List(string folder, CancellationToken ct = default)
    {
        var entries = new List<UsbEntry>();
        foreach (var dir in Directory.GetDirectories(folder).Where(d => !IsLink(d)).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(Folder(dir, System.IO.Path.GetFileName(dir)));
        }

        foreach (var file in Directory.GetFiles(folder).Where(f => !IsLink(f)).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(file);
                entries.Add(new UsbEntry(file, info.Name, false, info.Length, 1, KindOf(file), info.LastWriteTimeUtc,
                    Archives: DiskScanner.IsArchiveStart(info.Name) ? 1 : 0));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                entries.Add(new UsbEntry(file, System.IO.Path.GetFileName(file), false, 0, 1, UsbKind.File, default,
                    $"Can't be read: {e.Message}"));
            }
        }

        return entries;
    }

    private static UsbEntry Folder(string dir, string name)
    {
        try
        {
            var (size, files) = Measure(dir);
            return new UsbEntry(dir, name, true, size, files, KindOf(dir), Directory.GetLastWriteTimeUtc(dir),
                Archives: Unpack.ArchiveSets.Find(dir).Count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Count what can be read, and say that the rest can't (a copy would stop on it).
            long size = 0;
            var files = 0;
            try
            {
                _ = Directory.EnumerateFileSystemEntries(dir).Any(); // The folder itself can't be opened?
                foreach (var file in Files(dir, skipInaccessible: true))
                {
                    size += file.Length;
                    files++;
                }
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                return new UsbEntry(dir, name, true, 0, 0, UsbKind.Folder, default, $"Can't be read: {inner.Message}");
            }

            return new UsbEntry(dir, name, true, size, files, UsbKind.Folder, SafeTime(dir),
                $"Part of it can't be read: {e.Message}", Partial: true);
        }
    }

    private static DateTime SafeTime(string path)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary>Bytes and files under a path, symbolic links left out (the copy skips them too).</summary>
    public static (long Size, int Files) Measure(string path)
    {
        if (File.Exists(path))
        {
            return (new FileInfo(path).Length, 1);
        }

        long size = 0;
        var files = 0;
        foreach (var file in Files(path))
        {
            size += file.Length;
            files++;
        }

        return (size, files);
    }

    /// <summary>Every regular file under a folder, without following or copying symbolic links.</summary>
    public static IEnumerable<FileInfo> Files(string folder, bool skipInaccessible = false)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = skipInaccessible,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        return new DirectoryInfo(folder).EnumerateFiles("*", options).Where(f => f.LinkTarget == null);
    }

    public static UsbKind KindOf(string path)
    {
        if (File.Exists(path))
        {
            return path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? UsbKind.Disc
                : DiskScanner.IsVideo(path) ? UsbKind.Video
                : UsbKind.File;
        }

        if (UnpackPlan.Discs(path, 3).Count > 0)
        {
            return UsbKind.Disc;
        }

        try
        {
            return Files(path).Take(5000).Any(f => DiskScanner.IsVideo(f.Name)) ? UsbKind.Video : UsbKind.Folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return UsbKind.Folder;
        }
    }

    /// <summary>The folder a copy lands in: discs for ISOs and BDMV folders (Ripparr), import for the rest (Manual Import).</summary>
    public static string Folder(ScratcharrOptions options, UsbKind kind, CopyDestination destination) => destination switch
    {
        CopyDestination.Discs => options.DiscsFolder,
        CopyDestination.Import => options.ImportFolder,
        _ => kind == UsbKind.Disc ? options.DiscsFolder : options.ImportFolder
    };

    public static string Target(ScratcharrOptions options, string source, UsbKind kind, CopyDestination destination) =>
        System.IO.Path.Combine(Folder(options, kind, destination), System.IO.Path.GetFileName(Paths.Clean(source)));

    /// <summary>Null when the copy can go ahead, else the reason it can't.</summary>
    public static string? Problem(ScratcharrOptions options, string source, string target, long size, long freeBytes, IEnumerable<Job> jobs)
    {
        if (!options.AllowChanges)
        {
            return UnpackPlan.ChangesOff;
        }

        if (!Paths.IsUnder(source, options.UsbRoot) || IsLink(source))
        {
            return $"Only files and folders under {options.UsbRoot} are copied.";
        }

        if (!File.Exists(source) && !Directory.Exists(source))
        {
            return "It is not on the external disk any more.";
        }

        var name = System.IO.Path.GetFileName(target);
        if (!Paths.IsUnder(target, options.ImportFolder) && !Paths.IsUnder(target, options.DiscsFolder)
            || name.StartsWith('.') || name.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return $"The copy has to go into {options.ImportFolder} or {options.DiscsFolder}, under a name not starting with a dot.";
        }

        if (File.Exists(target) || Directory.Exists(target))
        {
            return $"{target} already exists.";
        }

        if (jobs.Any(j => j.Open && (j.Target == target || j.Kind == JobKind.Copy && j.Source == source)))
        {
            return "That copy is already queued.";
        }

        if (freeBytes > 0 && size + UnpackPlan.Headroom > freeBytes)
        {
            return $"Not enough space: it needs {Format.Size(size)} and {Format.Size(freeBytes)} is free.";
        }

        return null;
    }

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget != null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The mount a path sits on, to tell a mounted disk from an empty mount point.</summary>
    private static string? Device(string path) => Mounts.Of(path);
}
