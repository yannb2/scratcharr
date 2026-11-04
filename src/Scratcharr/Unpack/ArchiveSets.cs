using System.Text.RegularExpressions;
using Scratcharr.Scan;

namespace Scratcharr.Unpack;

/// <summary>
/// One archive, possibly split in parts: name.part01.rar + name.part02.rar..., name.rar + name.r00...,
/// name.7z.001 + name.7z.002..., or a single name.7z / name.zip. <see cref="Start"/> is the file 7-Zip opens.
/// </summary>
public sealed record ArchiveSet
{
    /// <summary>The scanned item (torrent folder, job folder or loose file) it was found in.</summary>
    public required string ItemPath { get; init; }

    public required string Start { get; init; }

    /// <summary>The name without the part number and extension: the default target folder name.</summary>
    public required string Name { get; init; }

    public IReadOnlyList<string> Parts { get; init; } = [];

    public long Size { get; init; }

    /// <summary>Where it sits inside the item, for display: "CD1/name.rar".</summary>
    public string RelativeStart => ItemPath == Start ? Path.GetFileName(Start) : Path.GetRelativePath(ItemPath, Start);
}

public static partial class ArchiveSets
{
    /// <summary>How deep inside an item archives are looked for (a torrent folder, then a CD1 or Disc 1 folder).</summary>
    public const int MaxDepth = 3;

    /// <summary>Every archive set in an item (a folder or one file), sorted by name.</summary>
    public static IReadOnlyList<ArchiveSet> Find(string itemPath)
    {
        if (File.Exists(itemPath))
        {
            var folder = Path.GetDirectoryName(itemPath)!;
            var name = Path.GetFileName(itemPath);
            if (!DiskScanner.IsArchiveStart(name))
            {
                return [];
            }

            var siblings = SafeFiles(folder).Select(Path.GetFileName).OfType<string>().ToList();
            return [Build(itemPath, folder, name, siblings)];
        }

        if (!Directory.Exists(itemPath))
        {
            return [];
        }

        var sets = new List<ArchiveSet>();
        Walk(itemPath, itemPath, 0, sets);
        return sets.OrderBy(s => s.Start, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Walk(string itemPath, string folder, int depth, List<ArchiveSet> sets)
    {
        var names = SafeFiles(folder).Select(Path.GetFileName).OfType<string>().ToList();
        foreach (var name in names.Where(DiskScanner.IsArchiveStart))
        {
            sets.Add(Build(itemPath, folder, name, names));
        }

        if (depth >= MaxDepth)
        {
            return;
        }

        foreach (var sub in SafeDirectories(folder))
        {
            Walk(itemPath, sub, depth + 1, sets);
        }
    }

    private static ArchiveSet Build(string itemPath, string folder, string start, IReadOnlyList<string> names)
    {
        var (name, isPart) = Split(start);
        var parts = names.Where(isPart).Order(StringComparer.OrdinalIgnoreCase).Select(n => Path.Combine(folder, n)).ToList();
        if (parts.Count == 0)
        {
            parts.Add(Path.Combine(folder, start));
        }

        return new ArchiveSet
        {
            ItemPath = itemPath,
            Start = Path.Combine(folder, start),
            Name = name,
            Parts = parts,
            Size = parts.Sum(p => SafeLength(p))
        };
    }

    /// <summary>The set's name and a test for the files that belong to it.</summary>
    internal static (string Name, Func<string, bool> IsPart) Split(string start)
    {
        var lower = start.ToLowerInvariant();
        var partRar = PartRar().Match(start);
        if (partRar.Success)
        {
            var stem = start[..partRar.Index];
            return (stem, n => Same(n, stem, PartRarEnding()));
        }

        if (lower.EndsWith(".rar"))
        {
            var stem = start[..^4];
            return (stem, n => Same(n, stem, OldRarPart()));
        }

        if (lower.EndsWith(".7z.001"))
        {
            var stem = start[..^7];
            return (stem, n => Same(n, stem, SevenZipPart()));
        }

        if (lower.EndsWith(".zip"))
        {
            var stem = start[..^4];
            return (stem, n => Same(n, stem, ZipPart()));
        }

        var plain = Path.GetFileNameWithoutExtension(start);
        return (plain, n => n.Equals(start, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The name is the stem followed by one of the part endings.</summary>
    private static bool Same(string name, string stem, Regex ending) =>
        name.Length > stem.Length && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && ending.IsMatch(name[stem.Length..]);

    [GeneratedRegex(@"\.part\d+\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex PartRar();

    [GeneratedRegex(@"^\.part\d+\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex PartRarEnding();

    [GeneratedRegex(@"^\.(rar|[r-z]\d\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex OldRarPart();

    [GeneratedRegex(@"^\.7z\.\d{3}$", RegexOptions.IgnoreCase)]
    private static partial Regex SevenZipPart();

    [GeneratedRegex(@"^\.(zip|z\d\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex ZipPart();

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try
        {
            return Directory.GetFiles(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try
        {
            return Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
