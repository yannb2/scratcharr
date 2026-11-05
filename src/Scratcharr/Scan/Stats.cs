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

    /// <summary>Items in the recycle folder and their size.</summary>
    public int Recycled { get; init; }

    public long RecycledBytes { get; init; }

    /// <summary>Jobs waiting, running (0 or 1), and finished in the last 24 hours, done or failed.</summary>
    public int JobsQueued { get; init; }

    public int JobsRunning { get; init; }
    public int JobsDone { get; init; }
    public int JobsFailed { get; init; }

    /// <summary>One line for a dashboard: "Copying Big.Show · 42%, 2 queued", "1 failed today", "Idle".</summary>
    public string JobStatus { get; init; } = "";

    public long TotalBytes { get; init; }

    /// <summary>Free share of the downloads pool, in percent (0 when unknown).</summary>
    public double FreePercent { get; init; }

    /// <summary>"1.20 TB free (8%)", with "Low: " in front when below the low space alert's threshold.</summary>
    public string Space { get; init; } = "";

    /// <summary>"To review" in one line: "5 items, 120 GB" or "Nothing".</summary>
    public string Review { get; init; } = "";

    public static Stats From(ScanResult? scan, bool scanning, ToolStatusService tools, int openJobs = 0,
        IReadOnlyList<Options.RecycledItem>? recycled = null, IReadOnlyList<Scratcharr.Jobs.Job>? jobs = null, int progress = 0,
        Clean.SpaceAlertSettings? space = null, DateTime? nowUtc = null, string remaining = "")
    {
        var now = nowUtc ?? DateTime.UtcNow;
        jobs ??= [];
        var running = jobs.FirstOrDefault(j => j.State == Scratcharr.Jobs.JobState.Running);
        var queued = jobs.Count(j => j.State == Scratcharr.Jobs.JobState.Queued);
        var recent = jobs.Where(j => j.Finished is { } f && f >= now.AddDays(-1)).ToList();
        var done = recent.Count(j => j.State == Scratcharr.Jobs.JobState.Done);
        var failed = recent.Count(j => j.State == Scratcharr.Jobs.JobState.Failed);
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
            Jobs = openJobs,
            Recycled = recycled?.Count ?? 0,
            RecycledBytes = recycled?.Sum(r => r.Size) ?? 0,
            JobsQueued = queued,
            JobsRunning = running == null ? 0 : 1,
            JobsDone = done,
            JobsFailed = failed,
            JobStatus = JobLine(running, progress, queued, done, failed, remaining),
            TotalBytes = scan?.TotalBytes ?? 0,
            FreePercent = scan is { TotalBytes: > 0 } s ? Math.Round(Clean.SpaceAlert.FreePercent(s), 1) : 0,
            Space = scan is { TotalBytes: > 0 } t
                ? $"{(Clean.SpaceAlert.Low(t, space ?? new Clean.SpaceAlertSettings()) ? "Low: " : "")}{Format.Size(t.FreeBytes)} free ({Clean.SpaceAlert.Percent(t)})"
                : "",
            Review = review.Count == 0 ? "Nothing" : $"{review.Count} item{(review.Count == 1 ? "" : "s")}, {Format.Size(review.Sum(i => i.Item.Size))}"
        };
    }

    public static string JobLine(Scratcharr.Jobs.Job? running, int progress, int queued, int done, int failed, string remaining = "")
    {
        var parts = new List<string>();
        if (running != null)
        {
            parts.Add($"{(running.Kind == Scratcharr.Jobs.JobKind.Copy ? "Copying" : "Unpacking")} {running.Name} · {progress}%"
                      + (remaining.Length > 0 ? $", {remaining.Split(" · ")[0]}" : ""));
        }

        if (queued > 0)
        {
            parts.Add($"{queued} queued");
        }

        if (failed > 0)
        {
            parts.Add($"{failed} failed today");
        }

        if (parts.Count == 0)
        {
            parts.Add(done > 0 ? $"Idle, {done} done today" : "Idle");
        }

        return string.Join(", ", parts);
    }
}
