using Scratcharr.Clean;
using Scratcharr.Options;
using Scratcharr.Scan;

namespace Scratcharr.Tests;

[TestClass]
public sealed class AutoRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ScratcharrOptions Options = new ScratcharrOptions { DownloadsRoot = "/downloads", AllowChanges = true }.WithDefaults();

    private static ItemReport Report(string name, Verdict verdict, int changedDaysAgo, int seenDaysAgo = 60, string category = "radarr",
        bool isProtected = false) => new()
    {
        Item = new DiskItem
        {
            Path = "/downloads/torrents/radarr/" + name, Name = name, Folder = "Torrents", Kind = FolderKind.Torrents,
            Category = category, Size = 100, Modified = Now.AddDays(-changedDaysAgo), Protected = isProtected
        },
        Verdict = verdict,
        FirstSeen = Now.AddDays(-seenDaysAgo)
    };

    private static List<AutoRule> On(params (Verdict Verdict, int Days)[] rules) =>
        AutoRules.Complete(rules.Select(r => new AutoRule { Verdict = r.Verdict, Days = r.Days, Enabled = true }).ToList());

    private static string Due(IReadOnlyList<AutoRule> rules, params ItemReport[] items) =>
        string.Join(",", AutoRules.Due(new ScanResult { Items = items }, rules, Options, Now).Select(d => d.Report.Item.Name));

    [TestMethod]
    public void Every_rule_starts_off()
    {
        var rules = AutoRules.Complete(null);
        Assert.HasCount(AutoRules.Choices.Length, rules);
        Assert.IsFalse(rules.Any(r => r.Enabled));
        Assert.AreEqual("", Due(rules, Report("old", Verdict.ImportedLeftover, 100)));
    }

    [TestMethod]
    public void Only_old_enough_items_of_an_enabled_verdict_are_due_oldest_first()
    {
        var rules = On((Verdict.ImportedLeftover, 3));
        Assert.AreEqual("older,old", Due(rules,
            Report("old", Verdict.ImportedLeftover, 4),
            Report("young", Verdict.ImportedLeftover, 2),
            Report("older", Verdict.ImportedLeftover, 10),
            Report("stray", Verdict.Stray, 100)));
    }

    [TestMethod]
    public void Age_never_counts_from_before_scratcharr_first_saw_it()
    {
        var rules = On((Verdict.Stray, 30));
        Assert.AreEqual("", Due(rules, Report("just-seen", Verdict.Stray, changedDaysAgo: 400, seenDaysAgo: 1)));
        Assert.AreEqual("seen", Due(rules, Report("seen", Verdict.Stray, changedDaysAgo: 400, seenDaysAgo: 31)));
    }

    [TestMethod]
    public void Manual_and_protected_items_are_never_due()
    {
        var rules = On((Verdict.Failed, 1), (Verdict.Stray, 1));
        Assert.AreEqual("", Due(rules,
            Report("manual", Verdict.Failed, 10, category: "manual"),
            Report("prowlarr", Verdict.Failed, 10, category: "prowlarr"),
            Report("locked", Verdict.Stray, 10, isProtected: true)));
    }

    [TestMethod]
    public void A_round_is_capped()
    {
        var rules = On((Verdict.Stray, 1));
        var items = Enumerable.Range(0, AutoRules.MaxPerRound + 10).Select(i => Report($"s{i}", Verdict.Stray, 5)).ToArray();
        Assert.HasCount(AutoRules.MaxPerRound, AutoRules.Due(new ScanResult { Items = items }, rules, Options, Now));
    }
}
