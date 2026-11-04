namespace Scratcharr.Scan;

/// <summary>
/// Sorting for the Downloads table: every column, both ways. Items without a value for the column (no torrent, no
/// arr app...) always go last, whichever way it sorts; ties keep name order.
/// </summary>
public static class ItemSort
{
    public const string DefaultKey = "size";

    public static readonly string[] Keys = ["name", "verdict", "client", "arr", "seeding", "size", "age", "leaves"];

    /// <summary>The way a column sorts on its first click: biggest and longest first, the rest A to Z or soonest first.</summary>
    public static bool DefaultDescending(string key) => key is "size" or "seeding";

    public static string Key(string? key) => key != null && Keys.Contains(key) ? key : DefaultKey;

    /// <summary>"asc" or "desc" from the URL, else the column's own default.</summary>
    public static bool Descending(string key, string? dir) => dir switch
    {
        "asc" => false,
        "desc" => true,
        _ => DefaultDescending(key)
    };

    public static IEnumerable<ItemReport> Apply(IEnumerable<ItemReport> items, string? key, string? dir)
    {
        key = Key(key);
        var descending = Descending(key, dir);
        var sorted = key switch
        {
            "name" => By(items, i => i.Item.Name, descending, StringComparer.OrdinalIgnoreCase),
            "verdict" => By(items, i => (int?)i.Verdict, descending),
            "client" => By(items, Client, descending, StringComparer.OrdinalIgnoreCase),
            "arr" => By(items, Arr, descending, StringComparer.OrdinalIgnoreCase),
            "seeding" => By(items, i => i.Torrents.FirstOrDefault(t => t.Finished)?.SeedingSeconds, descending),
            "age" => By(items, i => (DateTime?)i.Item.Modified, descending),
            "leaves" => By(items, Leaves, descending),
            _ => By(items, i => (long?)i.Item.Size, descending)
        };
        return sorted.ThenBy(i => i.Item.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The Client column's text: the torrent's state, else the SABnzbd job's.</summary>
    public static string? Client(ItemReport item) =>
        item.Torrents.FirstOrDefault()?.StateWord
        ?? (item.SabJobs.FirstOrDefault() is { } job ? (job.InQueue ? "In the queue" : job.Status) : null);

    public static string? Arr(ItemReport item)
    {
        var apps = item.Queue.Select(q => q.App).Concat(item.History.Select(h => h.App)).Distinct().ToList();
        return apps.Count == 0 ? null : string.Join(", ", apps);
    }

    /// <summary>When it leaves: its date; "after import" sorts after every date; "never" has no value (last).</summary>
    private static DateTime? Leaves(ItemReport item) => item.Removal switch
    {
        null or { Nobody: true } => null,
        { Due: { } due } => due,
        _ => DateTime.MaxValue
    };

    private static IOrderedEnumerable<ItemReport> By<T>(IEnumerable<ItemReport> items, Func<ItemReport, T?> value, bool descending,
        IComparer<T>? comparer = null) where T : class
    {
        var withValue = items.OrderBy(i => value(i) is null || value(i) is string { Length: 0 });
        return descending ? withValue.ThenByDescending(i => value(i)!, comparer) : withValue.ThenBy(i => value(i)!, comparer);
    }

    private static IOrderedEnumerable<ItemReport> By<T>(IEnumerable<ItemReport> items, Func<ItemReport, T?> value, bool descending)
        where T : struct
    {
        var withValue = items.OrderBy(i => value(i) is null);
        return descending ? withValue.ThenByDescending(i => value(i)) : withValue.ThenBy(i => value(i));
    }
}
