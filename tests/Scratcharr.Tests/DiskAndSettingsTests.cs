using Scratcharr.Clients;
using Scratcharr.Options;
using Scratcharr.Scan;
using Scratcharr.Tools;

namespace Scratcharr.Tests;

[TestClass]
public sealed class DiskAndSettingsTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "scratcharr-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private void File(string relative, int bytes = 10)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
    }

    private ScratcharrOptions Options() => new ScratcharrOptions { DownloadsRoot = _root }.WithDefaults();

    [TestMethod]
    public void Defaults_are_made_absolute_under_the_downloads_root()
    {
        var options = Options();
        Assert.AreEqual(Path.Combine(_root, "torrents"), options.Folders[0].Path);
        CollectionAssert.Contains(options.Protected, Path.Combine(_root, "ripparr"));
        Assert.AreEqual("/elsewhere", options.Absolute("/elsewhere/"));
    }

    [TestMethod]
    public void Category_folders_are_opened_and_watched_folders_listed_once()
    {
        File("torrents/radarr/Movie.A/movie.mkv", 100);
        File("torrents/radarr/Movie.B/movie.mkv", 50);
        File("torrents/loose.iso", 30);
        File("torrents/Something/inside/deep.bin", 5);
        File("usenet/complete/radarr/Movie.C/c.mkv");
        File("usenet/incomplete/Job/part.rar");
        File("import/home.mkv");
        File("ripparr/work/job-1/stream.hevc");
        File("film.iso");

        var items = DiskScanner.Scan(Options(), new HashSet<string> { "radarr" }, CancellationToken.None);
        var names = items.Select(i => $"{i.Folder}:{i.Category}:{i.Name}").OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.AreEqual(string.Join("|", new[]
        {
            "Downloads::film.iso",
            "Downloads::ripparr",
            "Import::home.mkv",
            "Torrents::Something",
            "Torrents::loose.iso",
            "Torrents:radarr:Movie.A",
            "Torrents:radarr:Movie.B",
            "Usenet, complete:radarr:Movie.C",
            "Usenet, incomplete::Job"
        }.OrderBy(n => n, StringComparer.Ordinal)), string.Join("|", names));

        var movie = items.Single(i => i.Name == "Movie.A");
        Assert.AreEqual(100, movie.Size);
        Assert.AreEqual(1, movie.Videos);
        Assert.IsTrue(items.Single(i => i.Name == "ripparr").Protected);
        Assert.AreEqual(1, items.Single(i => i.Name == "film.iso").Discs);
        Assert.AreEqual(1, items.Single(i => i.Name == "Job").Archives);
    }

    [TestMethod]
    public void Archive_sets_count_their_first_part_only()
    {
        Assert.IsTrue(DiskScanner.IsArchiveStart("film.part1.rar"));
        Assert.IsTrue(DiskScanner.IsArchiveStart("film.part01.rar"));
        Assert.IsFalse(DiskScanner.IsArchiveStart("film.part02.rar"));
        Assert.IsTrue(DiskScanner.IsArchiveStart("film.rar"));
        Assert.IsFalse(DiskScanner.IsArchiveStart("film.r00"));
        Assert.IsTrue(DiskScanner.IsArchiveStart("film.7z"));
        Assert.IsTrue(DiskScanner.IsArchiveStart("film.7z.001"));
        Assert.IsFalse(DiskScanner.IsArchiveStart("film.mkv"));
    }

    [TestMethod]
    public void Paths_relate_on_folder_boundaries_only()
    {
        Assert.IsTrue(Paths.IsUnder("/downloads/torrents/a", "/downloads/torrents"));
        Assert.IsFalse(Paths.IsUnder("/downloads/torrents2/a", "/downloads/torrents"));
        Assert.IsFalse(Paths.IsUnder("/downloads/torrents", "/downloads/torrents"));
        Assert.IsTrue(Paths.Related("/a/b", "/a/b/"));
        Assert.IsFalse(Paths.Related("", "/a"));
    }

    [TestMethod]
    public void Store_remembers_when_a_path_was_first_seen()
    {
        var store = new Store(Path.Combine(_root, "test.db"));
        var first = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        store.MarkSeen(["/a"], first);
        var seen = store.MarkSeen(["/a", "/b"], first.AddDays(1));
        Assert.AreEqual(first, seen["/a"]);
        Assert.AreEqual(first.AddDays(1), seen["/b"]);

        store.SetSetting("k", "v");
        Assert.AreEqual("v", store.Setting("k"));
        store.SetSetting("k", null);
        Assert.IsNull(store.Setting("k"));
    }

    [TestMethod]
    public void Settings_check_warns_about_gaps()
    {
        var evidence = new Evidence
        {
            Qbittorrent = Reading<QbittorrentState>.From(new QbittorrentState
            {
                DefaultSavePath = "/downloads/torrents",
                Categories = new Dictionary<string, string> { ["radarr"] = "", ["prowlarr"] = "" },
                Torrents = [new Torrent { Hash = "A", Name = "linux.iso", Category = "" }],
                Limits = new SeedingLimits { Minutes = 4320, Action = ShareLimitAction.Remove }
            }),
            Sabnzbd = Reading<SabnzbdState>.From(new SabnzbdState
            {
                CompleteFolder = "/downloads/usenet/complete",
                Categories = new Dictionary<string, string> { ["*"] = "", ["radarr"] = "radarr" },
                HistoryRetention = "keeps all"
            }),
            Arr = new Dictionary<ArrApp, Reading<ArrState>>
            {
                [ArrApp.Radarr] = Reading<ArrState>.From(new ArrState
                {
                    App = ArrApp.Radarr,
                    Clients =
                    [
                        new ArrDownloadClient { Name = "qBittorrent", Implementation = "QBittorrent", Enabled = true, Category = "radarr", RemoveCompleted = true, RemoveFailed = true },
                        new ArrDownloadClient { Name = "SABnzbd", Implementation = "Sabnzbd", Enabled = true, Category = "radarr", RemoveCompleted = false, RemoveFailed = true }
                    ]
                })
            }
        };
        var report = SettingsCheck.Build(new ScratcharrOptions { DownloadsRoot = "/downloads" }.WithDefaults(), evidence, []);
        var text = string.Join("\n", report.Findings.Select(f => f.Text));
        StringAssert.Contains(text, "keeps their files");
        StringAssert.Contains(text, "have no category");
        StringAssert.Contains(text, "Remove Completed off for SABnzbd");
        StringAssert.Contains(text, "no \"manual\" category");
        Assert.AreEqual(Severity.Warning, report.Findings[0].Severity);

        var sabRow = report.Categories.Single(r => r.Client == "SABnzbd");
        Assert.AreEqual("/downloads/usenet/complete/radarr", sabRow.Folder);
        Assert.IsTrue(sabRow.Nobody);
        Assert.AreEqual("Stop seeding after 72 h, then remove the torrent (files stay).", report.SeedingLimits);
        Assert.DoesNotContain("category \"prowlarr\"", text, "prowlarr is a manual category, not a forgotten one");
    }

    [TestMethod]
    public void Category_names_come_from_options_and_clients()
    {
        var evidence = new Evidence
        {
            Qbittorrent = Reading<QbittorrentState>.From(new QbittorrentState
            {
                Categories = new Dictionary<string, string> { ["anime"] = "/downloads/torrents/anime-folder" }
            })
        };
        var names = ScanService.CategoryNames(new ScratcharrOptions().WithDefaults(), evidence);
        CollectionAssert.IsSubsetOf(new[] { "radarr", "manual", "prowlarr", "anime", "anime-folder" }, names.ToList());
    }

    [TestMethod]
    public void Seven_zip_version_and_github_tags()
    {
        var tool = ToolCatalog.All.Single();
        Assert.AreEqual("26.05", ToolCatalog.InstalledVersion(tool, "7-Zip (z) 26.05 (x64) : Copyright (c) 1999-2026 Igor Pavlov"));
        Assert.AreEqual("26.05", ToolCatalog.GitHubTag("""[{"tag_name":"26.05"},{"tag_name":"26.04"},{"tag_name":"27.00","prerelease":true}]"""));
    }
}
