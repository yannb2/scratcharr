using Scratcharr.Clean;
using Scratcharr.Options;
using Scratcharr.Scan;

namespace Scratcharr.Tests;

[TestClass]
public sealed class SpaceAlertTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private const long Gb = 1L << 30;

    private static ItemReport Report(string name, Verdict verdict, long size, string category = "radarr") => new()
    {
        Item = new DiskItem
        {
            Path = "/downloads/torrents/radarr/" + name, Name = name, Folder = "Torrents", Kind = FolderKind.Torrents,
            Category = category, Size = size, Modified = Now
        },
        Verdict = verdict,
        FirstSeen = Now
    };

    private static ScanResult Scan(long freeGb, params ItemReport[] items) =>
        new() { FreeBytes = freeGb * Gb, TotalBytes = 1000 * Gb, Items = items };

    [TestMethod]
    public void The_alert_starts_off_and_waits_a_day_before_repeating()
    {
        var settings = new SpaceAlertSettings();
        Assert.IsFalse(settings.Enabled);
        Assert.IsFalse(SpaceAlert.Due(Scan(50), settings, null, Now));

        settings.Enabled = true;
        Assert.IsTrue(SpaceAlert.Due(Scan(50), settings, null, Now));
        Assert.IsFalse(SpaceAlert.Due(Scan(150), settings, null, Now), "15% free is above the 10% default");
        Assert.IsFalse(SpaceAlert.Due(Scan(50), settings, Now.AddHours(-5), Now));
        Assert.IsTrue(SpaceAlert.Due(Scan(50), settings, Now.AddHours(-24), Now));
        Assert.IsFalse(SpaceAlert.Due(Scan(50) with { Error = "qBittorrent did not answer" }, settings, null, Now));
        Assert.IsFalse(SpaceAlert.Due(Scan(0) with { TotalBytes = 0 }, settings, null, Now), "unknown size: no alert");
    }

    [TestMethod]
    public void The_message_lists_the_biggest_items_to_review_but_never_manual_or_protected_ones()
    {
        var scan = Scan(42,
            Report("small-stray", Verdict.Stray, 1 * Gb),
            Report("big-leftover", Verdict.ImportedLeftover, 80 * Gb),
            Report("huge-manual", Verdict.Stray, 500 * Gb, category: "manual"),
            Report("huge-protected", Verdict.Protected, 400 * Gb),
            Report("downloading", Verdict.Active, 300 * Gb));

        Assert.AreEqual("big-leftover,small-stray", string.Join(",", SpaceAlert.Candidates(scan).Select(i => i.Item.Name)));
        var text = SpaceAlert.Text(scan, new SpaceAlertSettings { Enabled = true }, [], 7);
        StringAssert.StartsWith(text, "Only 42.0 GB free of 1000 GB (4.2%, alert below 10%).");
        StringAssert.Contains(text, "- big-leftover (80.0 GB, imported, leftover)");
        Assert.IsFalse(text.Contains("huge", StringComparison.Ordinal), text);
    }
}
