using Scratcharr.Clients;
using Scratcharr.Options;
using Scratcharr.Scan;

namespace Scratcharr.Tests;

[TestClass]
public sealed class SortTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static ItemReport Report(string name, long size, Verdict verdict = Verdict.Manual, long? seedingHours = null,
        DateTime? due = null, bool nobody = false, int ageDays = 1) => new()
    {
        Item = new DiskItem
        {
            Path = "/downloads/torrents/" + name, Name = name, Folder = "Torrents", Kind = FolderKind.Torrents, Category = "",
            Size = size, Modified = Now.AddDays(-ageDays)
        },
        Verdict = verdict,
        Torrents = seedingHours is { } h
            ? [new Torrent { Hash = name, Name = name, State = "stalledUP", Progress = 1, SeedingSeconds = h * 3600, ContentPath = name, SavePath = "/" }]
            : [],
        Removal = due != null || nobody ? new Removal { Text = "", Due = due, Nobody = nobody } : null
    };

    private static readonly ItemReport[] Items =
    [
        Report("b", 300, Verdict.Stray, seedingHours: 10, due: Now.AddHours(5), ageDays: 3),
        Report("A", 100, Verdict.Active, seedingHours: 50, nobody: true, ageDays: 1),
        Report("c", 200, Verdict.Failed, due: Now.AddHours(1), ageDays: 2)
    ];

    private static string Names(string? key, string? dir) => string.Join("", ItemSort.Apply(Items, key, dir).Select(i => i.Item.Name));

    [TestMethod]
    public void Every_column_sorts_both_ways()
    {
        Assert.AreEqual("bcA", Names(null, null), "default: size, biggest first");
        Assert.AreEqual("Acb", Names("size", "asc"));
        Assert.AreEqual("Abc", Names("name", null));
        Assert.AreEqual("cbA", Names("name", "desc"));
        Assert.AreEqual("Acb", Names("verdict", "asc"));
        Assert.AreEqual("bcA", Names("verdict", "desc"));
        Assert.AreEqual("bcA", Names("age", "asc"), "oldest change first");
        Assert.AreEqual("Acb", Names("age", "desc"));
    }

    [TestMethod]
    public void Items_without_a_value_go_last_both_ways()
    {
        Assert.AreEqual("Abc", Names("seeding", null), "longest seeding first; c has no torrent");
        Assert.AreEqual("bAc", Names("seeding", "asc"));
        Assert.AreEqual("cbA", Names("leaves", null), "soonest first; A never leaves");
        Assert.AreEqual("bcA", Names("leaves", "desc"));
        Assert.AreEqual("Abc", Names("client", "asc"));
        Assert.AreEqual("Abc", Names("client", "desc"), "A and b tie on Seeding, then name; c has no client");
    }

    [TestMethod]
    public void An_unknown_column_falls_back_to_size()
    {
        Assert.AreEqual("bcA", Names("nonsense", null));
        Assert.AreEqual(ItemSort.DefaultKey, ItemSort.Key("nonsense"));
    }
}
