using Microsoft.Extensions.Logging.Abstractions;
using Scratcharr.Clients;
using Scratcharr.Jobs;
using Scratcharr.Options;
using Scratcharr.Scan;
using Scratcharr.Tools;
using Scratcharr.Unpack;
using Scratcharr.Usb;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Scratcharr.Tests;

[TestClass]
public sealed class PauseTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "scratcharr-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "downloads"));
        Directory.CreateDirectory(Path.Combine(_root, "pc"));
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private string File(string relative, int bytes, int seed = 0)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[bytes];
        new Random(seed).NextBytes(data);
        System.IO.File.WriteAllBytes(path, data);
        return path;
    }

    private ScratcharrOptions Options() => new ScratcharrOptions
    {
        DownloadsRoot = Path.Combine(_root, "downloads"),
        UsbRoot = Path.Combine(_root, "pc"),
        AllowChanges = true
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

    [TestMethod]
    public async Task The_gate_holds_until_resume_and_says_when_it_changes()
    {
        var gate = new PauseGate();
        var changes = new List<bool>();
        gate.Changed += changes.Add;
        Assert.IsTrue(gate.WaitAsync(CancellationToken.None).IsCompleted);

        gate.Pause();
        gate.Pause();
        var wait = gate.WaitAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.IsFalse(wait.IsCompleted);

        gate.Resume();
        gate.Resume();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { true, false }, changes);

        gate.Pause();
        using var cancel = new CancellationTokenSource();
        var cancelled = gate.WaitAsync(cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => cancelled);
        Assert.IsTrue(new PauseGate(paused: true).Paused);
    }

    [TestMethod]
    public async Task A_paused_copy_waits_where_it_is_and_finishes_at_resume()
    {
        var source = File("pc/usb01/Movie.mkv", 3_000_000, seed: 4);
        var target = Path.Combine(_root, "downloads", "import", "Movie.mkv.copying");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var gate = new PauseGate(paused: true);
        long done = 0;

        var copy = UsbCopy.CopyAsync(source, target, true, (d, _) => Interlocked.Exchange(ref done, d), _ => { }, CancellationToken.None, pause: gate);
        await Task.Delay(200);
        Assert.IsFalse(copy.IsCompleted);
        Assert.AreEqual(0, Interlocked.Read(ref done), "nothing is read while paused");

        gate.Resume();
        var result = await copy.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(result.Verified);
        CollectionAssert.AreEqual(System.IO.File.ReadAllBytes(source), System.IO.File.ReadAllBytes(target));
    }

    [TestMethod]
    public async Task Cancel_still_stops_a_paused_copy()
    {
        var source = File("pc/usb01/Movie.mkv", 1000);
        var target = Path.Combine(_root, "downloads", "import", "Movie.mkv.copying");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var cancel = new CancellationTokenSource();
        var copy = UsbCopy.CopyAsync(source, target, false, (_, _) => { }, _ => { }, cancel.Token, pause: new PauseGate(paused: true));
        await Task.Delay(50);
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => copy);
    }

    [TestMethod]
    public async Task Pause_all_holds_the_queue_also_after_a_restart_and_resume_all_runs_it()
    {
        var options = Options();
        File("pc/usb01/Show/S01E01.mkv", 2000, seed: 1);
        var source = Path.Combine(_root, "pc", "usb01", "Show");

        var queue = Queue(options);
        queue.PauseAll();
        Assert.IsNull(queue.QueueCopy(source, CopyDestination.Import));
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await queue.StartAsync(stop.Token);
        await Task.Delay(300);
        Assert.AreEqual(JobState.Queued, queue.Jobs().Single().State);
        Assert.AreEqual("", queue.Remaining());
        await queue.StopAsync(CancellationToken.None);

        // A new start (container restart) is still paused.
        var again = Queue(options);
        Assert.IsTrue(again.Paused);
        var finished = new TaskCompletionSource();
        again.Changed += () =>
        {
            if (again.Jobs().All(j => !j.Open))
            {
                finished.TrySetResult();
            }
        };
        await again.StartAsync(stop.Token);
        await Task.Delay(300);
        Assert.AreEqual(JobState.Queued, again.Jobs().Single().State);

        again.ResumeAll();
        Assert.IsFalse(again.Paused);
        await finished.Task.WaitAsync(stop.Token);
        await again.StopAsync(CancellationToken.None);
        var job = again.Jobs().Single();
        Assert.AreEqual(JobState.Done, job.State, job.Message);
        Assert.IsTrue(System.IO.File.Exists(Path.Combine(options.ImportFolder, "Show", "S01E01.mkv")));
        Assert.IsFalse(Queue(options).Paused, "Resume all is saved too");
    }

    private static readonly string? SevenZipTool = new[] { "7zz", "7z" }.FirstOrDefault(t =>
        Environment.GetEnvironmentVariable("PATH")!.Split(':').Any(d => System.IO.File.Exists(Path.Combine(d, t))));

    [TestMethod]
    public async Task A_paused_unpack_stops_7zip_and_carries_on_at_resume()
    {
        if (SevenZipTool == null || !OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("7-Zip is not installed here (CI installs the 7zip package).");
        }

        var source = File("work/Movie.iso", 64 << 20, seed: 7);
        var archive = Path.Combine(_root, "work", "Movie.7z");
        var made = await Arrkit.Processes.ToolRunner.RunAsync(SevenZipTool, ["a", "-mx0", archive, source], TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, made.ExitCode, made.Error);
        var target = Path.Combine(_root, "out.unpacking");
        var gate = new PauseGate(paused: true);

        var unpack = SevenZip.ExtractAsync(SevenZipTool, archive, target, _ => { }, _ => { }, CancellationToken.None, gate);
        await Task.Delay(1000);
        Assert.IsFalse(unpack.IsCompleted, "7-Zip is stopped while paused");

        gate.Resume();
        var result = await unpack.WaitAsync(TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.AreEqual(64L << 20, new FileInfo(Path.Combine(target, "Movie.iso")).Length);
    }

    [TestMethod]
    public void The_dashboard_line_says_paused()
    {
        var running = new Job { Kind = JobKind.Copy, Source = "/mnt/pc/usb01/Big.Show", Target = "/downloads/import/Big.Show", State = JobState.Running };
        Assert.AreEqual("Paused: Copying Big.Show · 42%, 2 queued", Stats.JobLine(running, 42, 2, 0, 0, "Paused", paused: true));
        Assert.AreEqual("Paused: Idle", Stats.JobLine(null, 0, 0, 0, 0, paused: true));
        Assert.AreEqual("Copying extras to Movie (2020) · 10%", Stats.JobLine(running with { Kind = JobKind.Extras, Target = "/movies/Movie (2020)" }, 10, 0, 0, 0));
    }
}
