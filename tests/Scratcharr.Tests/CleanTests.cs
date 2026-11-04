using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Scratcharr.Clean;
using Scratcharr.Clients;
using Scratcharr.Options;
using Scratcharr.Scan;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Scratcharr.Tests;

[TestClass]
public sealed class CleanTests
{
    private string _root = "";
    private string Downloads => Path.Combine(_root, "downloads");

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "scratcharr-clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Downloads);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private string File(string relative, int bytes = 10)
    {
        var path = Path.Combine(Downloads, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private ScratcharrOptions Options(bool allowChanges = true, string qbittorrent = "") => new ScratcharrOptions
    {
        DownloadsRoot = Downloads,
        AllowChanges = allowChanges,
        DatabasePath = Path.Combine(_root, "test.db"),
        Qbittorrent = new QbittorrentOptions { Url = qbittorrent }
    }.WithDefaults();

    private (Cleaner Cleaner, ScanService Scans, Store Store) Build(ScratcharrOptions options)
    {
        var store = new Store(options.DatabasePath);
        var live = new LiveSettings(MsOptions.Create(options), store);
        var qbt = new QbittorrentClient(live, NullLogger<QbittorrentClient>.Instance);
        var sab = new SabnzbdClient(live, NullLogger<SabnzbdClient>.Instance);
        var scans = new ScanService(MsOptions.Create(options), store, live, qbt, sab,
            new ArrClient(live, NullLogger<ArrClient>.Instance), NullLogger<ScanService>.Instance);
        return (new Cleaner(MsOptions.Create(options), store, scans, qbt, sab, NullLogger<Cleaner>.Instance), scans, store);
    }

    private static ItemReport Report(string path, Verdict verdict, FolderKind kind = FolderKind.Torrents, bool isProtected = false,
        IReadOnlyList<Torrent>? torrents = null, IReadOnlyList<SabJob>? jobs = null, IReadOnlyList<ArrMatch<ArrQueueItem>>? queue = null) => new()
    {
        Item = new DiskItem { Path = path, Name = Path.GetFileName(path), Folder = "Torrents", Kind = kind, Protected = isProtected, IsDirectory = true },
        Verdict = verdict,
        Torrents = torrents ?? [],
        SabJobs = jobs ?? [],
        Queue = queue ?? []
    };

    [TestMethod]
    public void The_rules_say_what_can_be_cleaned()
    {
        var options = Options();
        var path = Path.Combine(Downloads, "torrents", "manual", "Movie");
        string Text(ItemReport r) => $"{CleanPlan.Offer(r, options).Kind}: {CleanPlan.Offer(r, options).Text}";

        StringAssert.StartsWith(Text(Report(path, Verdict.Protected, isProtected: true)), "None: Protected");
        StringAssert.StartsWith(Text(Report(path, Verdict.Stray, FolderKind.UsenetIncomplete)), "None: SABnzbd's working folder");
        StringAssert.StartsWith(Text(Report(path, Verdict.Unknown)), "None: A client could not be read");
        var queued = new ArrMatch<ArrQueueItem>(ArrApp.Radarr, new ArrQueueItem { Title = "Movie" });
        StringAssert.StartsWith(Text(Report(path, Verdict.WaitingForImport, queue: [queued])), "None: Radarr is waiting to import it");
        var running = new SabJob { NzoId = "SABnzbd_nzo_1", Name = "Movie", InQueue = true };
        StringAssert.StartsWith(Text(Report(path, Verdict.Active, FolderKind.Usenet, jobs: [running])), "None: SABnzbd is still working");

        var whole = new Torrent { Hash = "AAA", Name = "Pack", ContentPath = Path.GetDirectoryName(path)!, Progress = 1 };
        StringAssert.StartsWith(Text(Report(path, Verdict.Manual, torrents: [whole])), "None: The torrent holds more");
        var own = new Torrent { Hash = "AAA", Name = "Movie", ContentPath = path, Progress = 0.5 };
        var removal = Text(Report(path, Verdict.Manual, torrents: [own]));
        StringAssert.StartsWith(removal, "RemoveTorrent: Removes the torrent from qBittorrent (without its files)");
        StringAssert.Contains(removal, "not finished downloading");

        var finished = new SabJob { NzoId = "SABnzbd_nzo_2", Name = "Movie", Status = "Completed" };
        StringAssert.StartsWith(Text(Report(path, Verdict.Stray, FolderKind.Usenet, jobs: [finished])),
            "Recycle: Moves it to the recycle folder, and removes its SABnzbd history entry. Kept 7 days");
        StringAssert.StartsWith(Text(Report(path, Verdict.Stray)), "Recycle: Moves it to the recycle folder. Kept 7 days");
    }

    [TestMethod]
    public void Recycle_targets_keep_the_path_and_never_collide()
    {
        var options = Options();
        var day = new DateTime(2026, 11, 4, 10, 0, 0);
        var path = Path.Combine(Downloads, "torrents", "manual", "Movie");
        var target = CleanPlan.Target(options, path, day, mount: null);
        Assert.AreEqual(Path.Combine(Downloads, ".recycle", "2026-11-04", "torrents", "manual", "Movie"), target);
        Directory.CreateDirectory(target);
        Assert.AreEqual(target + " (2)", CleanPlan.Target(options, path, day, mount: null));

        // torrents on its own mount: the bin is at the top of that mount, so the move stays a rename.
        var mount = Path.Combine(Downloads, "torrents");
        Assert.AreEqual(Path.Combine(mount, ".recycle", "2026-11-04", "torrents", "manual", "Movie"), CleanPlan.Target(options, path, day, mount));
        Assert.IsTrue(CleanPlan.InRecycle(options, Path.Combine(mount, ".recycle", "x")));
        Assert.IsFalse(CleanPlan.InRecycle(options, path));
    }

    [TestMethod]
    public void A_recycle_folder_anywhere_is_protected()
    {
        File("torrents/.recycle/2026-11-04/x.mkv");
        File("import/Film.mkv");
        var items = DiskScanner.Scan(Options(), new HashSet<string>(), CancellationToken.None);
        Assert.IsTrue(items.Single(i => i.Name == ".recycle").Protected);
        Assert.IsFalse(items.Single(i => i.Name == "Film.mkv").Protected);
    }

    [TestMethod]
    public async Task Recycle_restore_delete_and_purge()
    {
        var options = Options();
        var (cleaner, _, store) = Build(options);
        var film = File("import/Film (2021)/Film (2021).mkv", 500);
        var folder = Path.GetDirectoryName(film)!;

        var outcome = (await cleaner.CleanAsync([folder], CancellationToken.None)).Single();
        Assert.IsTrue(outcome.Ok, outcome.Message);
        Assert.IsFalse(Directory.Exists(folder));
        var recycled = cleaner.Recycled().Single();
        Assert.AreEqual(folder, recycled.Original);
        Assert.AreEqual(500, recycled.Size);
        Assert.AreEqual("Manual", recycled.Verdict);
        Assert.IsTrue(System.IO.File.Exists(Path.Combine(recycled.Path, "Film (2021).mkv")));
        StringAssert.Contains(recycled.Path, Path.Combine(".recycle", DateTime.Now.ToString("yyyy-MM-dd"), "import"));

        // Something new at the old place blocks the restore; once it is gone the item goes back.
        Directory.CreateDirectory(folder);
        StringAssert.Contains(cleaner.Restore(recycled.Id), "already at");
        Directory.Delete(folder);
        Assert.IsNull(cleaner.Restore(recycled.Id));
        Assert.IsTrue(System.IO.File.Exists(film));
        Assert.IsEmpty(cleaner.Recycled());
        Assert.IsFalse(Directory.Exists(Path.Combine(Downloads, ".recycle", DateTime.Now.ToString("yyyy-MM-dd"))), "empty dated folders are tidied");

        // Recycle again, then delete for good.
        Assert.IsTrue((await cleaner.CleanAsync([folder], CancellationToken.None)).Single().Ok);
        var again = cleaner.Recycled().Single();
        var (count, bytes, error) = cleaner.Delete([again.Id]);
        Assert.AreEqual((1, 500L, (string?)null), (count, bytes, error));
        Assert.IsFalse(Directory.Exists(again.Path));

        // The purge deletes only what is older than RecycleDays.
        var loose = File("loose.iso", 50);
        Assert.IsTrue((await cleaner.CleanAsync([loose], CancellationToken.None)).Single().Ok);
        Assert.AreEqual(0, cleaner.Purge(DateTime.UtcNow.AddDays(6)).Count);
        Assert.AreEqual(1, cleaner.Purge(DateTime.UtcNow.AddDays(7.1)).Count);
        Assert.IsEmpty(cleaner.Recycled());

        var log = string.Join("|", store.Actions().Select(a => $"{a.Action}:{a.Ok}"));
        Assert.AreEqual("Purge:True|Recycle:True|Delete:True|Recycle:True|Restore:True|Restore:False|Recycle:True", log, "newest first");
    }

    [TestMethod]
    public async Task Nothing_changes_when_changes_are_off_or_the_item_is_claimed()
    {
        var film = File("import/Film.mkv");
        var (off, _, _) = Build(Options(allowChanges: false));
        StringAssert.Contains((await off.CleanAsync([film], CancellationToken.None)).Single().Message, "Changes are off");
        Assert.IsTrue(System.IO.File.Exists(film));

        // A torrent folder while qBittorrent can't be read: Unknown, so it stays.
        var torrent = File("torrents/radarr/Movie.2020/movie.mkv");
        var (cleaner, _, _) = Build(Options(qbittorrent: "http://127.0.0.1:9"));
        var outcome = (await cleaner.CleanAsync([Path.GetDirectoryName(torrent)!], CancellationToken.None)).Single();
        Assert.IsFalse(outcome.Ok);
        StringAssert.Contains(outcome.Message, "could not be read");
        Assert.IsTrue(System.IO.File.Exists(torrent));
        StringAssert.Contains((await cleaner.CleanAsync([Path.Combine(Downloads, "gone")], CancellationToken.None)).Single().Message, "not there");
    }

    [TestMethod]
    public async Task A_torrent_is_removed_in_qbittorrent_before_its_files_are_recycled()
    {
        var movie = Path.GetDirectoryName(File("torrents/manual/Movie.2020/movie.mkv", 100))!;
        using var qbt = new FakeQbittorrent(movie);
        var (cleaner, _, store) = Build(Options(qbittorrent: qbt.Url));

        var outcome = (await cleaner.CleanAsync([movie], CancellationToken.None)).Single();
        Assert.IsTrue(outcome.Ok, outcome.Message);
        Assert.AreEqual("Recycled; torrent removed from qBittorrent.", outcome.Message);
        Assert.AreEqual("hashes=abc123&deleteFiles=false", qbt.Deleted);
        Assert.IsFalse(Directory.Exists(movie));
        Assert.AreEqual("torrent removed from qBittorrent", cleaner.Recycled().Single().Note);
        CollectionAssert.AreEqual(new[] { "Recycle", "Remove torrent" }, store.Actions().Select(a => a.Action).ToArray());
    }

    [TestMethod]
    public void Summary_text_and_when_it_is_due()
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        DiskItem Item(string name, long size) => new() { Path = "/downloads/torrents/" + name, Name = name, Folder = "Torrents", Size = size };
        var scan = new ScanResult
        {
            Items =
            [
                new ItemReport { Item = Item("Old.Stray", 5L << 30), Verdict = Verdict.Stray, FirstSeen = now.AddDays(-30) },
                new ItemReport { Item = Item("New.Stray", 2L << 30), Verdict = Verdict.Stray, FirstSeen = now.AddDays(-2) },
                new ItemReport { Item = Item("Leftover", 1L << 30), Verdict = Verdict.ImportedLeftover, FirstSeen = now.AddDays(-1) },
                new ItemReport { Item = Item("Seeding", 1L << 30), Verdict = Verdict.Active, FirstSeen = now }
            ],
            FreeBytes = 100L << 30,
            TotalBytes = 1L << 40
        };
        var recycled = new[] { new RecycledItem { Original = "/downloads/x", Path = "/downloads/.recycle/x", Size = 3L << 30 } };
        var week = new[] { new ActionEntry(1, now, "Recycle", "/downloads/x", 3L << 30, "", true), new ActionEntry(2, now, "Purge", "/downloads/y", 1L << 30, "", true) };

        var text = Summary.Text(scan, recycled, week, now);
        Assert.AreEqual("""
            To review: 3 items, 8.00 GB: 2 strays (7.00 GB), 1 imported leftover (1.00 GB).
            New this week: 2 items, 3.00 GB (biggest: New.Stray).
            Recycle folder: 1 item, 3.00 GB.
            This week: 1 recycled, 1 deleted for good (1.00 GB).
            Free: 100 GB of 1.00 TB.
            """, text);

        var ntfy = new NtfyOptions { Day = DayOfWeek.Monday, Hour = 9 };
        Assert.AreEqual(new DateTime(2026, 11, 2, 9, 0, 0), Summary.LastDue(ntfy, new DateTime(2026, 11, 4, 12, 0, 0)));
        Assert.AreEqual(new DateTime(2026, 11, 2, 9, 0, 0), Summary.LastDue(ntfy, new DateTime(2026, 11, 9, 8, 59, 0)));
        Assert.AreEqual(new DateTime(2026, 11, 9, 9, 0, 0), Summary.LastDue(ntfy, new DateTime(2026, 11, 9, 9, 0, 0)));
    }

    /// <summary>Just enough of qBittorrent's API: one torrent that owns the folder, and the delete call.</summary>
    private sealed class FakeQbittorrent : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _content;
        private bool _removed;

        public string Url { get; }
        public string? Deleted { get; private set; }

        public FakeQbittorrent(string content)
        {
            _content = content;
            var port = new Random().Next(20000, 40000);
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(Serve);
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                var path = context.Request.Url!.AbsolutePath;
                var body = path switch
                {
                    "/api/v2/app/version" => "v5.1.0",
                    "/api/v2/torrents/info" => _removed ? "[]" : System.Text.Json.JsonSerializer.Serialize(new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["hash"] = "abc123", ["name"] = "Movie.2020", ["category"] = "manual", ["state"] = "stoppedUP",
                            ["content_path"] = _content, ["save_path"] = Path.GetDirectoryName(_content)!, ["progress"] = 1,
                            ["total_size"] = 100
                        }
                    }),
                    "/api/v2/torrents/categories" => "{}",
                    "/api/v2/app/preferences" => "{}",
                    "/api/v2/torrents/delete" => Delete(context.Request),
                    _ => null
                };
                context.Response.StatusCode = body == null ? 404 : 200;
                var bytes = Encoding.UTF8.GetBytes(body ?? "");
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        private string Delete(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream);
            Deleted = reader.ReadToEnd();
            _removed = true;
            return "";
        }

        public void Dispose() => _listener.Close();
    }
}
