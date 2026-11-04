using Scratcharr.Tools;

namespace Scratcharr.Scan;

/// <summary>The numbers /api/stats gives a dashboard (Homepage's custom API widget).</summary>
public sealed record Stats
{
    public int Items { get; init; }
    public int Strays { get; init; }
    public long StrayBytes { get; init; }

    /// <summary>Items worth a look: strays, imported leftovers, failed, half-finished.</summary>
    public int ToReview { get; init; }

    public long ToReviewBytes { get; init; }
    public int Active { get; init; }
    public int Manual { get; init; }
    public long UsedBytes { get; init; }
    public long FreeBytes { get; init; }
    public bool Scanning { get; init; }
    public DateTime? LastScan { get; init; }
    public int ToolUpdates { get; init; }

    /// <summary>Items holding archives (Unpack page).</summary>
    public int Archives { get; init; }

    /// <summary>Unpack jobs queued or running.</summary>
    public int Jobs { get; init; }

    public static Stats From(ScanResult? scan, bool scanning, ToolStatusService tools, int openJobs = 0)
    {
        var items = scan?.Items ?? [];
        var review = items.Where(i => i.Verdict.NeedsReview()).ToList();
        var strays = items.Where(i => i.Verdict == Verdict.Stray).ToList();
        return new Stats
        {
            Items = items.Count,
            Strays = strays.Count,
            StrayBytes = strays.Sum(i => i.Item.Size),
            ToReview = review.Count,
            ToReviewBytes = review.Sum(i => i.Item.Size),
            Active = items.Count(i => i.Verdict == Verdict.Active),
            Manual = items.Count(i => i.Verdict == Verdict.Manual),
            UsedBytes = items.Sum(i => i.Item.Size),
            FreeBytes = scan?.FreeBytes ?? 0,
            Scanning = scanning,
            LastScan = scan?.FinishedAt,
            ToolUpdates = tools.UpdateCount,
            Archives = items.Count(i => Scratcharr.Jobs.UnpackPlan.Offers(i.Item)),
            Jobs = openJobs
        };
    }
}
