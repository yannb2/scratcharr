using Microsoft.Extensions.Logging.Abstractions;
using Scratcharr.Clients;
using Scratcharr.Jobs;
using Scratcharr.Options;
using Scratcharr.Scan;
using Scratcharr.Unpack;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Scratcharr.Tests;

[TestClass]
public sealed class UnpackTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "scratcharr-unpack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private string File(string relative, int bytes = 10)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private ScratcharrOptions Options(bool allowChanges = true) =>
        new ScratcharrOptions { DownloadsRoot = _root, AllowChanges = allowChanges, Tools = new ToolPaths { SevenZip = Tool ?? "7zz" } }.WithDefaults();

    /// <summary>7zz (the image's 7-Zip), or 7z from a distribution package.</summary>
    private static readonly string? Tool = new[] { "7zz", "7z" }.FirstOrDefault(t =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => System.IO.File.Exists(Path.Combine(d, t))));

    [TestMethod]
    public void Sets_group_their_parts_and_ignore_other_files()
    {
        File("torrents/Movie/Movie.part01.rar", 100);
        File("torrents/Movie/Movie.part02.rar", 100);
        File("torrents/Movie/Movie.part10.rar", 50);
        File("torrents/Movie/Movie.nfo");
        File("torrents/Movie/Subs/Subs.rar", 5);
        File("torrents/Movie/Subs/Subs.r00", 5);
        File("torrents/Movie/Subs/Subs.r01", 5);
        File("torrents/Movie/Other.7z.001", 7);
        File("torrents/Movie/Other.7z.002", 7);
        File("torrents/Movie/Other2.7z.001", 1);

        var sets = ArchiveSets.Find(Path.Combine(_root, "torrents/Movie"));
        var text = string.Join("|", sets.Select(s => $"{s.RelativeStart}:{s.Name}:{s.Parts.Count}:{s.Size}"));
        Assert.AreEqual("Movie.part01.rar:Movie:3:250|Other.7z.001:Other:2:14|Other2.7z.001:Other2:1:1|Subs/Subs.rar:Subs:3:15", text);
    }

    [TestMethod]
    public void A_loose_archive_is_its_own_set()
    {
        var zip = File("torrents/Concert.zip", 20);
        var set = ArchiveSets.Find(zip).Single();
        Assert.AreEqual("Concert", set.Name);
        Assert.AreEqual("Concert.zip", set.RelativeStart);
        Assert.AreEqual(20, set.Size);
        Assert.IsEmpty(ArchiveSets.Find(File("torrents/movie.mkv")));
    }

    [TestMethod]
    public void Listing_is_read_from_the_technical_output()
    {
        const string output = """
            7-Zip 26.05 (x64) : Copyright (c) 1999-2026 Igor Pavlov

            Scanning the drive for archives:
            1 file, 1000 bytes (1 KiB)

            Listing archive: Movie.part01.rar

            --
            Path = Movie.part01.rar
            Type = Rar5
            Physical Size = 1000

            ----------
            Path = Movie
            Folder = +
            Size = 0
            Attributes = D

            Path = Movie/Movie.iso
            Folder = -
            Size = 50000000000
            Attributes = A

            Path = Movie\BDMV\index.bdmv
            Folder = -
            Size = 120

            """;
        var listing = new ArchiveListing { Entries = SevenZip.ParseListing(output) };
        Assert.HasCount(3, listing.Entries);
        Assert.AreEqual(50000000120, listing.UnpackedSize);
        Assert.AreEqual(1, listing.Isos);
        Assert.IsTrue(listing.HasBdmv, string.Join("\n", listing.Entries));
        Assert.AreEqual("1 ISO, a BDMV folder, 2 files in all", listing.Summary);
    }

    [TestMethod]
    public void Progress_and_reasons()
    {
        Assert.AreEqual(42, SevenZip.Percent("  42% 3 - Movie/Movie.iso"));
        Assert.AreEqual(100, SevenZip.Percent("100%"));
        Assert.IsNull(SevenZip.Percent("- Movie/Movie.iso"));
        Assert.AreEqual("The archive has a password.", SevenZip.Reason(2, "ERROR: Wrong password : Movie.iso"));
        Assert.AreEqual("A part of the archive is missing or unfinished.", SevenZip.Reason(2, "ERRORS:\nMissing volume : Movie.part03.rar"));
        Assert.AreEqual("7-Zip is not installed (7zz not found).", SevenZip.Reason(SevenZip.NotStartedCode, ""));
        StringAssert.StartsWith(SevenZip.Reason(2, "ERROR: concert.part01.rar : Cannot open the file as archive\n\nErrors: 1"), "7-Zip can't open it");
    }

    [TestMethod]
    public void Targets_stay_inside_the_discs_folder()
    {
        var options = Options();
        Assert.AreEqual(Path.Combine(_root, "discs", "Movie"), UnpackPlan.Target(options, " Movie "));
        Assert.IsNull(UnpackPlan.Target(options, "../etc"));
        Assert.IsNull(UnpackPlan.Target(options, "a/b"));
        Assert.IsNull(UnpackPlan.Target(options, ".hidden"));
        Assert.IsNull(UnpackPlan.Target(options, "Movie.unpacking"));
        Assert.IsNull(UnpackPlan.Target(options, ""));
    }

    [TestMethod]
    public void Problems_stop_an_unpack()
    {
        var start = File("torrents/Movie/Movie.part1.rar");
        var options = Options();
        var target = UnpackPlan.Target(options, "Movie");

        Assert.AreEqual(UnpackPlan.ChangesOff, UnpackPlan.Problem(Options(allowChanges: false), start, target, 10, 0, []));
        Assert.IsNull(UnpackPlan.Problem(options, start, target, 10, 0, []));
        StringAssert.Contains(UnpackPlan.Problem(options, start, target, 10L << 30, 5L << 30, []), "Not enough space");
        StringAssert.Contains(UnpackPlan.Problem(options, start, target, 10, 0,
            [new Job { Source = start, Target = target!, State = JobState.Queued }]), "already queued");
        StringAssert.Contains(UnpackPlan.Problem(options, start + "x", target, 10, 0, []), "not there");

        File("discs/Movie/old.iso");
        StringAssert.Contains(UnpackPlan.Problem(options, start, target, 10, 0, []), "already exists");
    }

    [TestMethod]
    public void Discs_found_for_ripparr_and_links()
    {
        File("discs/A/A.iso");
        File("discs/B/Movie/BDMV/index.bdmv");
        File("discs/B/Movie/BDMV/STREAM/00001.m2ts");
        var discs = UnpackPlan.Discs(Path.Combine(_root, "discs"));
        CollectionAssert.AreEqual(new[] { Path.Combine(_root, "discs/A/A.iso"), Path.Combine(_root, "discs/B/Movie") }, discs.ToList());

        var options = Options();
        Assert.IsNull(UnpackPlan.RipparrLink(options, "/downloads/discs/A/A.iso"));
        options.RipparrUrl = "http://10.1.72.11:8186/";
        Assert.AreEqual("http://10.1.72.11:8186/disc?path=%2Fdownloads%2Fdiscs%2FA%20B%2FA.iso", UnpackPlan.RipparrLink(options, "/downloads/discs/A B/A.iso"));
    }

    [TestMethod]
    public void Partial_folders_are_protected_from_the_scan()
    {
        File("discs/Movie.unpacking/Movie.iso");
        var items = DiskScanner.Scan(Options(), new HashSet<string>(), CancellationToken.None);
        Assert.IsTrue(items.Single(i => i.Name == "Movie.unpacking").Protected);
    }

    [TestMethod]
    public async Task A_split_archive_is_listed_and_unpacked_by_a_job()
    {
        if (Tool == null)
        {
            Assert.Inconclusive("7-Zip is not installed here (CI installs the 7zip package).");
        }

        // A 7z split into 3 volumes, like a multi-part release.
        var source = Path.Combine(_root, "source", "Movie");
        Directory.CreateDirectory(Path.Combine(source, "BDMV", "STREAM"));
        var payload = new byte[300_000];
        new Random(1).NextBytes(payload);
        await System.IO.File.WriteAllBytesAsync(Path.Combine(source, "BDMV", "STREAM", "00001.m2ts"), payload);
        await System.IO.File.WriteAllTextAsync(Path.Combine(source, "BDMV", "index.bdmv"), "INDX");
        var item = Path.Combine(_root, "torrents", "manual", "Movie.Release");
        Directory.CreateDirectory(item);
        var made = await Arrkit.Processes.ToolRunner.RunAsync(Tool, ["a", "-mx0", "-v120k", Path.Combine(item, "Movie.7z"), source], TimeSpan.FromMinutes(1));
        Assert.IsTrue(made.Success, made.Error + made.Output);

        var set = ArchiveSets.Find(item).Single();
        Assert.AreEqual("Movie.7z.001", set.RelativeStart);
        Assert.HasCount(3, set.Parts);

        var listing = await SevenZip.ListAsync(Tool, set.Start, CancellationToken.None);
        Assert.IsNull(listing.Error);
        Assert.IsTrue(listing.HasBdmv, string.Join("\n", listing.Entries));
        Assert.AreEqual(300_004, listing.UnpackedSize);

        var options = Options();
        var store = new Store(Path.Combine(_root, "test.db"));
        var live = new LiveSettings(MsOptions.Create(options), store);
        var scans = new ScanService(MsOptions.Create(options), store,
            new QbittorrentClient(live, NullLogger<QbittorrentClient>.Instance),
            new SabnzbdClient(live, NullLogger<SabnzbdClient>.Instance),
            new ArrClient(live, NullLogger<ArrClient>.Instance),
            NullLogger<ScanService>.Instance);
        var queue = new JobQueue(MsOptions.Create(options), store, scans, NullLogger<JobQueue>.Instance);
        var finished = new TaskCompletionSource();
        queue.Changed += () =>
        {
            if (queue.Jobs().All(j => !j.Open))
            {
                finished.TrySetResult();
            }
        };

        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await queue.StartAsync(stop.Token);
        Assert.IsNull(queue.QueueUnpack(set, "Movie (2020)", listing));
        await finished.Task.WaitAsync(stop.Token);
        await queue.StopAsync(CancellationToken.None);

        var job = queue.Jobs().Single();
        Assert.AreEqual(JobState.Done, job.State, job.Message + "\n" + job.Log);
        var target = Path.Combine(_root, "discs", "Movie (2020)");
        Assert.AreEqual(target, job.Target);
        CollectionAssert.AreEqual(payload, await System.IO.File.ReadAllBytesAsync(Path.Combine(target, "Movie", "BDMV", "STREAM", "00001.m2ts")));
        Assert.IsFalse(Directory.Exists(target + UnpackPlan.PartialSuffix));
        Assert.AreEqual("Unpacked: 1 disc for Ripparr.", job.Message);
        Assert.HasCount(3, Directory.GetFiles(item), "the archive parts stay");

        // The same name again is refused: the folder exists now.
        StringAssert.Contains(queue.QueueUnpack(set, "Movie (2020)", listing), "already exists");
    }

    [TestMethod]
    public async Task A_broken_archive_is_reported()
    {
        if (Tool == null)
        {
            Assert.Inconclusive("7-Zip is not installed here (CI installs the 7zip package).");
        }

        var item = Path.Combine(_root, "torrents", "Broken");
        Directory.CreateDirectory(item);
        await System.IO.File.WriteAllBytesAsync(Path.Combine(item, "Broken.7z"), [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4, 1, 2, 3]);
        var set = ArchiveSets.Find(item).Single();
        var listing = await SevenZip.ListAsync(Tool, set.Start, CancellationToken.None);
        Assert.IsNotNull(listing.Error);

        var target = Path.Combine(_root, "discs", "Broken");
        var result = await SevenZip.ExtractAsync(Tool, set.Start, target + UnpackPlan.PartialSuffix, _ => { }, _ => { }, CancellationToken.None);
        Assert.AreNotEqual(0, result.ExitCode);
    }
}
