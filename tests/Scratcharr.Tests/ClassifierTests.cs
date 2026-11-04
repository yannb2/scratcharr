using Scratcharr.Clients;
using Scratcharr.Options;
using Scratcharr.Scan;

namespace Scratcharr.Tests;

[TestClass]
public sealed class ClassifierTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static DiskItem Item(string path, FolderKind kind = FolderKind.Torrents, string category = "radarr", int parts = 0, bool isProtected = false) => new()
    {
        Path = path,
        Name = Path.GetFileName(path),
        Folder = kind.ToString(),
        Kind = kind,
        Category = category,
        IsDirectory = true,
        Size = 1000,
        Modified = Now.AddDays(-3),
        PartFiles = parts,
        Protected = isProtected
    };

    private static Torrent Torrent(string contentPath, string state = "stalledUP", double progress = 1, long seedingHours = 10,
        string category = "radarr", string hash = "AAA") => new()
    {
        Hash = hash,
        Name = Path.GetFileName(contentPath),
        Category = category,
        State = state,
        ContentPath = contentPath,
        SavePath = Path.GetDirectoryName(contentPath)!,
        Progress = progress,
        SeedingSeconds = seedingHours * 3600
    };

    private static ArrState Radarr(IEnumerable<ArrHistoryEvent>? history = null, IEnumerable<ArrQueueItem>? queue = null,
        bool removeCompleted = true, bool sabRemoveCompleted = false) => new()
    {
        App = ArrApp.Radarr,
        History = history?.ToList() ?? [],
        Queue = queue?.ToList() ?? [],
        Clients =
        [
            new ArrDownloadClient { Name = "qBittorrent", Implementation = "QBittorrent", Enabled = true, Category = "radarr", RemoveCompleted = removeCompleted, RemoveFailed = true },
            new ArrDownloadClient { Name = "SABnzbd", Implementation = "Sabnzbd", Enabled = true, Category = "radarr", RemoveCompleted = sabRemoveCompleted, RemoveFailed = true }
        ]
    };

    private static Evidence Evidence(IEnumerable<Torrent>? torrents = null, ArrState? radarr = null, SabnzbdState? sab = null,
        ShareLimitAction action = ShareLimitAction.Stop, bool qbtDown = false) => new()
    {
        Qbittorrent = qbtDown
            ? Reading<QbittorrentState>.Failed("down")
            : Reading<QbittorrentState>.From(new QbittorrentState
            {
                Torrents = torrents?.ToList() ?? [],
                Limits = new SeedingLimits { Minutes = 4320, Action = action }
            }),
        Sabnzbd = sab == null ? Reading<SabnzbdState>.NotConfigured() : Reading<SabnzbdState>.From(sab),
        Arr = new Dictionary<ArrApp, Reading<ArrState>> { [ArrApp.Radarr] = Reading<ArrState>.From(radarr ?? Radarr()) }
    };

    private static ItemReport Classify(DiskItem item, Evidence evidence) => Classifier.Classify(item, evidence, Now, Now);

    [TestMethod]
    public void A_seeding_torrent_is_active_and_leaves_after_its_limit()
    {
        var path = "/downloads/torrents/radarr/Movie";
        var report = Classify(Item(path), Evidence([Torrent(path, seedingHours: 52)]));
        Assert.AreEqual(Verdict.Active, report.Verdict);
        Assert.AreEqual(Now.AddHours(20), report.Removal!.Due);
        Assert.IsFalse(report.Removal.Nobody);
        StringAssert.Contains(report.Removal.Text, "Radarr removes it");
    }

    [TestMethod]
    public void A_stopped_torrent_is_seeding_done()
    {
        var path = "/downloads/torrents/radarr/Movie";
        var report = Classify(Item(path), Evidence([Torrent(path, state: "stoppedUP", seedingHours: 72)]));
        Assert.AreEqual(Verdict.SeedingDone, report.Verdict);
    }

    [TestMethod]
    public void A_torrent_without_category_and_no_arr_app_stays()
    {
        var path = "/downloads/torrents/linux.iso";
        var report = Classify(Item(path, category: ""), Evidence([Torrent(path, state: "stoppedUP", category: "")]));
        Assert.AreEqual(Verdict.SeedingDone, report.Verdict);
        Assert.IsTrue(report.Removal!.Nobody);
        Assert.IsTrue(report.StaysForever);
    }

    [TestMethod]
    public void Remove_without_files_at_the_limit_leaves_the_files()
    {
        var path = "/downloads/torrents/radarr/Movie";
        var report = Classify(Item(path), Evidence([Torrent(path)], action: ShareLimitAction.Remove));
        Assert.IsTrue(report.Removal!.Nobody);
        StringAssert.Contains(report.Removal.Text, "leaves its files");
    }

    [TestMethod]
    public void An_unfinished_torrent_file_is_matched_by_its_real_name()
    {
        var file = "/downloads/torrents/sonarr/Show.S01E01.mkv";
        var report = Classify(Item(file + ".!qB", category: "sonarr", parts: 1), Evidence([Torrent(file, state: "downloading", progress: 0.4, category: "sonarr")]));
        Assert.AreEqual(Verdict.Active, report.Verdict);
    }

    [TestMethod]
    public void Part_files_no_torrent_owns_are_half_finished()
    {
        var report = Classify(Item("/downloads/torrents/sonarr/Show.S01E01.mkv.!qB", category: "sonarr", parts: 1), Evidence());
        Assert.AreEqual(Verdict.HalfFinished, report.Verdict);
    }

    [TestMethod]
    public void Imported_and_no_torrent_left_is_a_leftover()
    {
        var path = "/downloads/torrents/radarr/Old.Movie";
        var radarr = Radarr(history:
        [
            new ArrHistoryEvent { EventType = "downloadFolderImported", DownloadId = "GONE", Date = Now.AddDays(-11), DroppedPath = path + "/Old.Movie.mkv" }
        ]);
        var report = Classify(Item(path), Evidence(radarr: radarr));
        Assert.AreEqual(Verdict.ImportedLeftover, report.Verdict);
        Assert.IsTrue(report.Verdict.NeedsReview());
        Assert.IsTrue(report.Removal!.Nobody);
    }

    [TestMethod]
    public void Nothing_claims_it_means_stray()
    {
        var report = Classify(Item("/downloads/torrents/Random.Thing", category: ""), Evidence());
        Assert.AreEqual(Verdict.Stray, report.Verdict);
    }

    [TestMethod]
    public void When_qbittorrent_cannot_be_read_a_torrent_folder_is_not_a_stray()
    {
        var report = Classify(Item("/downloads/torrents/Random.Thing", category: ""), Evidence(qbtDown: true));
        Assert.AreEqual(Verdict.Unknown, report.Verdict);
    }

    [TestMethod]
    public void The_manual_category_and_hand_filled_folders_are_manual()
    {
        Assert.AreEqual(Verdict.Manual, Classify(Item("/downloads/torrents/manual/Concert", category: "manual"), Evidence()).Verdict);
        Assert.AreEqual(Verdict.Manual, Classify(Item("/downloads/import/Home.mkv", FolderKind.Manual, ""), Evidence()).Verdict);
    }

    [TestMethod]
    public void A_manual_torrent_that_seeds_is_active_but_counted_as_manual()
    {
        var path = "/downloads/torrents/manual/Concert";
        var report = Classify(Item(path, category: "manual"), Evidence([Torrent(path, category: "manual")]));
        Assert.AreEqual(Verdict.Active, report.Verdict);
        Assert.IsTrue(report.IsManual);
        Assert.IsFalse(report.StaysForever);
    }

    [TestMethod]
    public void Protected_folders_are_never_judged()
    {
        var report = Classify(Item("/downloads/ripparr", FolderKind.Manual, "", isProtected: true), Evidence());
        Assert.AreEqual(Verdict.Protected, report.Verdict);
        Assert.IsNull(report.Removal);
    }

    [TestMethod]
    public void An_arr_queue_item_waiting_for_import()
    {
        var path = "/downloads/torrents/radarr/Movie";
        var radarr = Radarr(queue: [new ArrQueueItem { DownloadId = "AAA", TrackedState = "importPending", OutputPath = path }]);
        var report = Classify(Item(path), Evidence([Torrent(path)], radarr));
        Assert.AreEqual(Verdict.WaitingForImport, report.Verdict);
    }

    [TestMethod]
    public void Usenet_jobs_from_sabnzbd_history()
    {
        var done = "/downloads/usenet/complete/radarr/Movie.C";
        var failed = "/downloads/usenet/complete/sonarr/Show.S02";
        var sab = new SabnzbdState
        {
            History =
            [
                new SabJob { NzoId = "SABnzbd_nzo_c", Name = "Movie.C", Category = "radarr", Status = "Completed", Path = done },
                new SabJob { NzoId = "SABnzbd_nzo_s", Name = "Show.S02", Category = "sonarr", Status = "Failed", Path = failed, FailMessage = "password" }
            ]
        };
        var radarr = Radarr(history: [new ArrHistoryEvent { EventType = "downloadFolderImported", DownloadId = "SABNZBD_NZO_C", Date = Now.AddDays(-6) }]);
        var evidence = Evidence(radarr: radarr, sab: sab);

        var imported = Classify(Item(done, FolderKind.Usenet), evidence);
        Assert.AreEqual(Verdict.ImportedLeftover, imported.Verdict);
        StringAssert.Contains(imported.Removal!.Text, "Remove Completed off");

        Assert.AreEqual(Verdict.Failed, Classify(Item(failed, FolderKind.Usenet, "sonarr"), evidence).Verdict);
    }

    [TestMethod]
    public void A_queued_usenet_job_is_active()
    {
        var path = "/downloads/usenet/incomplete/Job.D";
        var sab = new SabnzbdState { Queue = [new SabJob { NzoId = "x", Name = "Job.D", InQueue = true, Status = "Downloading", Path = path }] };
        Assert.AreEqual(Verdict.Active, Classify(Item(path, FolderKind.UsenetIncomplete, ""), Evidence(sab: sab)).Verdict);
    }

    [TestMethod]
    public void A_torrent_saved_straight_into_its_category_folder_does_not_claim_everything_there()
    {
        // No-subfolder layout: content_path is the save path itself.
        var torrent = new Torrent { Hash = "A", Name = "x", ContentPath = "/downloads/torrents/radarr", SavePath = "/downloads/torrents/radarr" };
        Assert.IsFalse(Classifier.Claims(torrent, Item("/downloads/torrents/radarr/Other")));
    }

    [TestMethod]
    public void Ids_are_stable_and_short()
    {
        Assert.AreEqual(Ids.For("/downloads/x"), Ids.For("/downloads/x"));
        Assert.AreNotEqual(Ids.For("/downloads/x"), Ids.For("/downloads/y"));
        Assert.AreEqual(16, Ids.For("/downloads/x").Length);
    }
}
