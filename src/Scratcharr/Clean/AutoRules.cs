using Scratcharr.Options;
using Scratcharr.Scan;

namespace Scratcharr.Clean;

/// <summary>One automatic clean-up rule: recycle items with this verdict once they are old enough.</summary>
public sealed class AutoRule
{
    public Verdict Verdict { get; set; }
    public bool Enabled { get; set; }
    public int Days { get; set; }
}

/// <summary>
/// The opt-in rules the hourly clean-up round applies. Every rule is off until it is turned on on the Settings page.
/// They only ever do what the Recycle button does (through the 7-day recycle folder, never on manual or protected
/// items) and at most <see cref="MaxPerRound"/> items an hour.
/// </summary>
public static class AutoRules
{
    public const int MaxPerRound = 25;

    /// <summary>The verdicts a rule can cover, with the days it starts with.</summary>
    public static readonly (Verdict Verdict, int Days, string Help)[] Choices =
    [
        (Verdict.ImportedLeftover, 3, "An arr app imported a copy and no client owns the download any more."),
        (Verdict.SeedingDone, 2, "The torrent stopped at its seeding limit; it is removed from qBittorrent (files kept), then recycled."),
        (Verdict.Failed, 7, "The download or the import failed."),
        (Verdict.HalfFinished, 7, "Unfinished torrent files that no torrent owns."),
        (Verdict.Stray, 30, "Nothing claims it at all.")
    ];

    public static List<AutoRule> Defaults() =>
        Choices.Select(c => new AutoRule { Verdict = c.Verdict, Days = c.Days, Enabled = false }).ToList();

    /// <summary>The saved rules, completed with the defaults for any verdict they don't mention.</summary>
    public static List<AutoRule> Complete(IReadOnlyList<AutoRule>? saved) =>
        Choices.Select(c => saved?.FirstOrDefault(r => r.Verdict == c.Verdict) is { } rule
                ? new AutoRule { Verdict = c.Verdict, Enabled = rule.Enabled, Days = Math.Max(1, rule.Days) }
                : new AutoRule { Verdict = c.Verdict, Days = c.Days })
            .ToList();

    /// <summary>
    /// How long an item has qualified: since it last changed on disk, and never counting the time before Scratcharr
    /// first saw it (so turning a rule on doesn't sweep everything at once).
    /// </summary>
    public static TimeSpan Age(ItemReport report, DateTime nowUtc) =>
        nowUtc - (report.Item.Modified > report.FirstSeen ? report.Item.Modified : report.FirstSeen);

    /// <summary>The items the rules recycle now, oldest first, at most MaxPerRound.</summary>
    public static IReadOnlyList<(ItemReport Report, AutoRule Rule)> Due(ScanResult scan, IReadOnlyList<AutoRule> rules,
        ScratcharrOptions options, DateTime nowUtc) =>
        scan.Items
            .Where(i => !i.IsManual)
            .Select(i => (Report: i, Rule: rules.FirstOrDefault(r => r.Enabled && r.Verdict == i.Verdict)))
            .Where(x => x.Rule != null && Age(x.Report, nowUtc) >= TimeSpan.FromDays(x.Rule.Days))
            .Where(x => CleanPlan.Offer(x.Report, options).Allowed)
            .OrderByDescending(x => Age(x.Report, nowUtc))
            .Take(MaxPerRound)
            .Select(x => (x.Report, x.Rule!))
            .ToList();

    public static string Reason(AutoRule rule) => $"Auto rule: {rule.Verdict.Label()} after {rule.Days} day{(rule.Days == 1 ? "" : "s")}";
}
