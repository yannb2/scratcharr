using Scratcharr.Options;

namespace Scratcharr.Scan;

/// <summary>A folder /downloads should have, and why.</summary>
public sealed record ExpectedFolder(string Path, string Why, bool Exists);

/// <summary>
/// The layout /downloads should have: the watched folders, each download client category's folder, and the
/// protected folders of other apps. Create makes the missing ones (only under /downloads, never anything else).
/// </summary>
public static class FolderStructure
{
    public static IReadOnlyList<ExpectedFolder> Expected(ScratcharrOptions options, SettingsReport report, IReadOnlyList<string> protectedFolders)
    {
        var wanted = new List<(string Path, string Why)>();
        foreach (var folder in options.Folders)
        {
            wanted.Add((folder.Path, folder.Name));
        }

        foreach (var row in report.Categories.Where(r => r.Category.Length > 0 && r.Category != "(none)"))
        {
            wanted.Add((Paths.Clean(row.Folder), $"{row.Client} category {row.Category}"));
        }

        var recycle = System.IO.Path.GetFileName(options.RecycleFolder);
        foreach (var path in protectedFolders.Where(p => System.IO.Path.GetFileName(p) != recycle))
        {
            wanted.Add((path, "protected: another app's folder"));
        }

        return wanted
            .Where(w => Paths.IsUnder(w.Path, options.DownloadsRoot))
            .GroupBy(w => w.Path)
            .Select(g => new ExpectedFolder(g.Key, string.Join("; ", g.Select(w => w.Why).Distinct()), Directory.Exists(g.Key)))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Creates the missing folders; returns the ones made and the first error.</summary>
    public static (IReadOnlyList<string> Made, string? Error) Create(ScratcharrOptions options, IEnumerable<ExpectedFolder> folders)
    {
        var made = new List<string>();
        foreach (var folder in folders.Where(f => !f.Exists && Paths.IsUnder(f.Path, options.DownloadsRoot)))
        {
            try
            {
                Directory.CreateDirectory(folder.Path);
                made.Add(folder.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return (made, $"{folder.Path}: {e.Message}");
            }
        }

        return (made, null);
    }
}
