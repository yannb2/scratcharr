using Microsoft.Extensions.Logging.Abstractions;
using Scratcharr.Clients;
using Scratcharr.Jobs;
using Scratcharr.Options;
using Scratcharr.Scan;
using Scratcharr.Usb;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Scratcharr.Tests;

[TestClass]
public sealed class UsbTests
{
    private string _root = "";
    private string _usb = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "scratcharr-usb-" + Guid.NewGuid().ToString("N"));
        _usb = Path.Combine(_root, "pc");
        Directory.CreateDirectory(Path.Combine(_root, "downloads"));
        Directory.CreateDirectory(_usb);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private string File(string relative, int bytes = 10, int seed = 0)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[bytes];
        new Random(seed).NextBytes(data);
        System.IO.File.WriteAllBytes(path, data);
        return path;
    }

    private ScratcharrOptions Options(bool allowChanges = true, bool verify = true) => new ScratcharrOptions
    {
        DownloadsRoot = Path.Combine(_root, "downloads"),
        UsbRoot = _usb,
        AllowChanges = allowChanges,
        VerifyCopies = verify
    }.WithDefaults();

    private JobQueue Queue(ScratcharrOptions options)
    {
        var store = new Store(Path.Combine(_root, "test.db"));
        var live = new LiveSettings(MsOptions.Create(options), store);
        var scans = new ScanService(MsOptions.Create(options), store, live,
            new QbittorrentClient(live, NullLogger<QbittorrentClient>.Instance),
            new SabnzbdClient(live, NullLogger<SabnzbdClient>.Instance),
            new ArrClient(live, NullLogger<ArrClient>.Instance),
            NullLogger<ScanService>.Instance);
        return new JobQueue(MsOptions.Create(options), store, scans, NullLogger<JobQueue>.Instance);
    }

    /// <summary>Queues the jobs, starts the queue, and waits until no job is open.</summary>
    private static async Task RunAll(JobQueue queue, Action add)
    {
        var finished = new TaskCompletionSource();
        queue.Changed += () =>
        {
            if (queue.Jobs().All(j => !j.Open))
            {
                finished.TrySetResult();
            }
        };

        add();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await queue.StartAsync(stop.Token);
        await finished.Task.WaitAsync(stop.Token);
        await queue.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public void Options_make_the_import_folder_absolute()
    {
        var options = Options();
        Assert.AreEqual(Path.Combine(_root, "downloads", "import"), options.ImportFolder);
        Assert.AreEqual(Path.Combine(_root, "downloads", "discs"), options.DiscsFolder);
    }

    [TestMethod]
    public void Auto_sends_discs_to_discs_and_the_rest_to_import()
    {
        var options = Options();
        var iso = File("pc/usb01/Movie (2020).iso");
        File("pc/usb01/Concert/BDMV/index.bdmv");
        File("pc/usb01/Show/Season 01/Show S01E01.mkv");
        File("pc/usb01/Notes/readme.txt");
        var mkv = File("pc/usb01/Film.mkv");

        var entries = CopyPlan.List(Path.Combine(_usb, "usb01")).ToDictionary(e => e.Name);
        Assert.AreEqual("Concert:Disc|Notes:Folder|Show:Video|Film.mkv:Video|Movie (2020).iso:Disc",
            string.Join("|", entries.Values.Select(e => $"{e.Name}:{e.Kind}")));
        Assert.AreEqual(10, entries["Show"].Size);
        Assert.AreEqual(1, entries["Show"].Files);

        Assert.AreEqual(Path.Combine(options.DiscsFolder, "Movie (2020).iso"), CopyPlan.Target(options, iso, UsbKind.Disc, CopyDestination.Auto));
        Assert.AreEqual(Path.Combine(options.ImportFolder, "Film.mkv"), CopyPlan.Target(options, mkv, UsbKind.Video, CopyDestination.Auto));
        Assert.AreEqual(Path.Combine(options.ImportFolder, "Movie (2020).iso"), CopyPlan.Target(options, iso, UsbKind.Disc, CopyDestination.Import));
        Assert.AreEqual(Path.Combine(options.DiscsFolder, "Film.mkv"), CopyPlan.Target(options, mkv, UsbKind.Video, CopyDestination.Discs));
    }

    [TestMethod]
    public void Archives_on_an_external_disk_are_counted_and_can_be_unpacked()
    {
        var options = Options();
        File("pc/usb01/Movie.Release/Movie.part1.rar");
        File("pc/usb01/Movie.Release/Movie.part2.rar");
        File("pc/usb01/Movie.Release/Extras/extras.zip");
        var single = File("pc/usb01/Concert.7z");
        File("pc/usb01/Film.mkv");

        var entries = CopyPlan.List(Path.Combine(_usb, "usb01")).ToDictionary(e => e.Name);
        Assert.AreEqual(2, entries["Movie.Release"].Archives);
        Assert.AreEqual(1, entries["Concert.7z"].Archives);
        Assert.AreEqual(0, entries["Film.mkv"].Archives);

        var target = Jobs.UnpackPlan.Target(options, "Concert");
        Assert.IsTrue(Jobs.UnpackPlan.FromExternal(options, single));
        Assert.IsNull(Jobs.UnpackPlan.Problem(options, single, target, 10, 0, []));

        var elsewhere = File("other/Concert.7z");
        StringAssert.Contains(Jobs.UnpackPlan.Problem(options, elsewhere, target, 10, 0, []), "Only archives under");
    }

    [TestMethod]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void An_unreadable_folder_is_listed_with_the_reason_and_the_rest_still_shows()
    {
        File("pc/d/Film.mkv");
        File("pc/d/Movies/Movie.mkv", 20);
        File("pc/d/Movies/Locked/secret.bin");
        var system = Path.Combine(_usb, "d", "System Volume Information");
        Directory.CreateDirectory(system);
        var locked = Path.Combine(_usb, "d", "Movies", "Locked");
        System.IO.File.SetUnixFileMode(system, UnixFileMode.None);
        System.IO.File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (Environment.UserName == "root")
            {
                Assert.Inconclusive("root reads every folder, so nothing is unreadable here.");
            }

            var entries = CopyPlan.List(Path.Combine(_usb, "d")).ToDictionary(e => e.Name);
            Assert.HasCount(3, entries);
            Assert.IsNull(entries["Film.mkv"].Unreadable);
            Assert.StartsWith("Can't be read", entries["System Volume Information"].Unreadable!);
            Assert.StartsWith("Part of it can't be read", entries["Movies"].Unreadable!);
            Assert.AreEqual(20, entries["Movies"].Size);
            Assert.AreEqual(1, entries["Movies"].Files);
            Assert.IsTrue(entries["Movies"].Partial);
            Assert.IsFalse(entries["System Volume Information"].Partial);
        }
        finally
        {
            const UnixFileMode all = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            System.IO.File.SetUnixFileMode(system, all);
            System.IO.File.SetUnixFileMode(locked, all);
        }
    }

    [TestMethod]
    public void Disks_are_the_folders_under_the_usb_root()
    {
        File("pc/usb01/Film.mkv");
        Directory.CreateDirectory(Path.Combine(_usb, "usb02"));
        File("pc/iso-mounts/Movie/BDMV/index.bdmv");
        var disks = CopyPlan.Disks(Options());
        Assert.AreEqual("usb01:True|usb02:False", string.Join("|", disks.Select(d => $"{d.Name}:{d.Mounted}")));
        var missing = Options();
        missing.UsbRoot = Path.Combine(_root, "missing");
        Assert.IsEmpty(CopyPlan.Disks(missing));
    }

    [TestMethod]
    public void Problems_stop_a_copy()
    {
        var options = Options();
        var mkv = File("pc/usb01/Film.mkv");
        var target = Path.Combine(options.ImportFolder, "Film.mkv");

        StringAssert.Contains(CopyPlan.Problem(Options(allowChanges: false), mkv, target, 10, 0, []), "Changes are off");
        StringAssert.Contains(CopyPlan.Problem(options, File("downloads/torrents/x.mkv"), target, 10, 0, []), "Only files and folders under");
        StringAssert.Contains(CopyPlan.Problem(options, Path.Combine(_usb, "usb01", "gone.mkv"), target, 10, 0, []), "not on the external disk");
        StringAssert.Contains(CopyPlan.Problem(options, mkv, Path.Combine(_root, "elsewhere", "Film.mkv"), 10, 0, []), "has to go into");
        StringAssert.Contains(CopyPlan.Problem(options, mkv, Path.Combine(options.ImportFolder, ".hidden"), 10, 0, []), "has to go into");
        StringAssert.Contains(CopyPlan.Problem(options, mkv, target, 10, 1L << 30, []), "Not enough space");
        var queued = new Job { Kind = JobKind.Copy, Source = mkv, Target = target, State = JobState.Queued };
        StringAssert.Contains(CopyPlan.Problem(options, mkv, target, 10, 0, [queued]), "already queued");
        Assert.IsNull(CopyPlan.Problem(options, mkv, target, 10, 0, []));

        File("downloads/import/Film.mkv");
        StringAssert.Contains(CopyPlan.Problem(options, mkv, target, 10, 0, []), "already exists");
    }

    [TestMethod]
    public void Partial_copies_are_protected_from_the_scan()
    {
        File("downloads/import/Film.mkv.copying");
        var items = DiskScanner.Scan(Options(), new HashSet<string>(), CancellationToken.None);
        Assert.IsTrue(items.Single(i => i.Name == "Film.mkv.copying").Protected);
    }

    [TestMethod]
    public async Task A_folder_and_a_file_are_copied_checked_and_kept_on_the_usb_disk()
    {
        var options = Options();
        var stream = File("pc/usb01/Movie.Disc/BDMV/STREAM/00001.m2ts", 9_000_000, seed: 1);
        File("pc/usb01/Movie.Disc/BDMV/index.bdmv", 4, seed: 2);
        Directory.CreateDirectory(Path.Combine(_usb, "usb01", "Movie.Disc", "CERTIFICATE"));
        System.IO.File.CreateSymbolicLink(Path.Combine(_usb, "usb01", "Movie.Disc", "link.m2ts"), stream);
        var old = new DateTime(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        System.IO.File.SetLastWriteTimeUtc(stream, old);
        var mkv = File("pc/usb01/Film (2021).mkv", 5000, seed: 3);
        var iso = File("pc/usb01/Old (1999).iso", 50, seed: 4);

        var queue = Queue(options);
        await RunAll(queue, () =>
        {
            Assert.IsNull(queue.QueueCopy(Path.Combine(_usb, "usb01", "Movie.Disc"), CopyDestination.Auto));
            Assert.IsNull(queue.QueueCopy(mkv, CopyDestination.Auto));
            Assert.IsNull(queue.QueueCopy(iso, CopyDestination.Auto));
        });

        var jobs = queue.Jobs().OrderBy(j => j.Id).ToList();
        Assert.IsTrue(jobs.All(j => j.State == JobState.Done), string.Join("\n", jobs.Select(j => j.Message + "\n" + j.Log)));
        Assert.AreEqual($"Copied 2 files, {Format.Size(9_000_004)}, every file checked. 1 disc for Ripparr.", jobs[0].Message);
        StringAssert.Contains(jobs[1].Message, "Ready for Manual Import");
        Assert.AreEqual("Copied 1 file, 50 B, every file checked. 1 disc for Ripparr.", jobs[2].Message);

        var disc = Path.Combine(options.DiscsFolder, "Movie.Disc");
        CollectionAssert.AreEqual(System.IO.File.ReadAllBytes(stream), System.IO.File.ReadAllBytes(Path.Combine(disc, "BDMV", "STREAM", "00001.m2ts")));
        Assert.AreEqual(old, System.IO.File.GetLastWriteTimeUtc(Path.Combine(disc, "BDMV", "STREAM", "00001.m2ts")));
        Assert.IsTrue(Directory.Exists(Path.Combine(disc, "CERTIFICATE")), "empty folders are copied");
        Assert.IsFalse(System.IO.File.Exists(Path.Combine(disc, "link.m2ts")), "symbolic links are skipped");
        Assert.IsFalse(Directory.Exists(disc + CopyPlan.PartialSuffix));
        CollectionAssert.AreEqual(System.IO.File.ReadAllBytes(mkv), System.IO.File.ReadAllBytes(Path.Combine(options.ImportFolder, "Film (2021).mkv")));
        Assert.IsTrue(System.IO.File.Exists(mkv), "the USB disk keeps its files");
        Assert.HasCount(1, Directory.GetFileSystemEntries(options.ImportFolder));

        // The same copy again is refused: it is there now.
        StringAssert.Contains(queue.QueueCopy(mkv, CopyDestination.Auto), "already exists");
    }

    [TestMethod]
    public async Task A_copy_cut_short_carries_on_from_the_files_it_had_not_finished()
    {
        var source = Path.Combine(_usb, "usb01", "Show");
        File("pc/usb01/Show/S01E01.mkv", 3000, seed: 1);
        File("pc/usb01/Show/S01E02.mkv", 3000, seed: 2);
        File("pc/usb01/Show/S01E03.mkv", 3000, seed: 3);
        var target = Path.Combine(_root, "downloads", "import", "Show.copying");

        // First run: stopped right after the first file is done.
        var done = new List<CopiedFile>();
        using (var stop = new CancellationTokenSource())
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => UsbCopy.CopyAsync(source, target, true, (_, _) => { }, _ => { },
                stop.Token, fileDone: f =>
                {
                    done.Add(f);
                    stop.Cancel();
                }));
        }

        Assert.HasCount(1, done);
        Assert.AreEqual("S01E01.mkv", done[0].Path);
        Assert.AreEqual(done[0], CopiedFile.Parse(done[0].Line));

        // A half-written second file is copied again; the first one is kept.
        System.IO.File.WriteAllBytes(Path.Combine(target, "S01E02.mkv"), new byte[100]);
        var log = new List<string>();
        var result = await UsbCopy.CopyAsync(source, target, true, (_, _) => { }, log.Add, CancellationToken.None, done);
        Assert.AreEqual(1, result.Resumed);
        Assert.IsTrue(result.Verified);
        Assert.IsFalse(log.Any(l => l.Contains("Copy S01E01", StringComparison.Ordinal)), string.Join("|", log));
        foreach (var name in new[] { "S01E01.mkv", "S01E02.mkv", "S01E03.mkv" })
        {
            CollectionAssert.AreEqual(System.IO.File.ReadAllBytes(Path.Combine(source, name)), System.IO.File.ReadAllBytes(Path.Combine(target, name)), name);
        }

        // An original that changed since is copied again.
        System.IO.File.SetLastWriteTimeUtc(Path.Combine(source, "S01E01.mkv"), DateTime.UtcNow.AddDays(-1));
        Assert.AreEqual(0, (await UsbCopy.CopyAsync(source, target + "2", false, (_, _) => { }, _ => { }, CancellationToken.None, done)).Resumed);
    }

    [TestMethod]
    public async Task A_copy_still_running_at_start_up_is_queued_again_and_finishes()
    {
        var options = Options();
        File("pc/usb01/Show/S01E01.mkv", 2000, seed: 1);
        File("pc/usb01/Show/S01E02.mkv", 2000, seed: 2);
        var queue = Queue(options);
        var store = new Store(Path.Combine(_root, "test.db"));
        var source = Path.Combine(_usb, "usb01", "Show");
        var target = Path.Combine(options.ImportFolder, "Show");
        // As the container left it: the job marked running, the first episode copied and listed.
        var first = new List<CopiedFile>();
        using (var stop = new CancellationTokenSource())
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => UsbCopy.CopyAsync(source, target + CopyPlan.PartialSuffix, false,
                (_, _) => { }, _ => { }, stop.Token, fileDone: f =>
                {
                    first.Add(f);
                    stop.Cancel();
                }));
        }

        var id = store.AddJob(new Job { Kind = JobKind.Copy, Source = source, Target = target, State = JobState.Running, Created = DateTime.UtcNow, Size = 4000 });
        store.SetSetting($"job.{id}.copied", first[0].Line + "\n");

        await RunAll(queue, () => { });

        var job = queue.Jobs().Single();
        Assert.AreEqual(JobState.Done, job.State, job.Message);
        StringAssert.Contains(job.Message, "1 files kept");
        Assert.IsTrue(System.IO.File.Exists(Path.Combine(target, "S01E02.mkv")));
        Assert.IsFalse(Directory.Exists(target + CopyPlan.PartialSuffix));
        Assert.IsNull(store.Setting($"job.{id}.copied"));
    }

    [TestMethod]
    public async Task A_copy_that_cannot_finish_leaves_nothing_behind()
    {
        var options = Options();
        File("pc/usb01/Show/S01E01.mkv", 1000);
        File("pc/usb01/Show/S01E02.mkv", 1000);
        var queue = Queue(options);
        await RunAll(queue, () =>
        {
            Assert.IsNull(queue.QueueCopy(Path.Combine(_usb, "usb01", "Show"), CopyDestination.Auto));
            // The disk is unplugged before the job runs: it fails on the second check.
            Directory.Delete(Path.Combine(_usb, "usb01", "Show"), true);
        });

        var job = queue.Jobs().Single();
        Assert.AreEqual(JobState.Failed, job.State);
        StringAssert.Contains(job.Message, "not on the external disk");
        Assert.IsFalse(Directory.Exists(Path.Combine(options.ImportFolder, "Show" + CopyPlan.PartialSuffix)));
    }
}
