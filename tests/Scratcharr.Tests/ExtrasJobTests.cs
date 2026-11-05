using Arrkit.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Scratcharr.Clients;
using Scratcharr.Jobs;
using Scratcharr.Options;
using Scratcharr.Scan;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Scratcharr.Tests;

/// <summary>"Copy as extras": videos from the downloads or an external disk into a library folder.</summary>
[TestClass]
public sealed class ExtrasJobTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Directory.CreateTempSubdirectory("scratcharr-extras-").FullName;
        Directory.CreateDirectory(Path.Combine(_root, "downloads"));
        Directory.CreateDirectory(Path.Combine(_root, "pc"));
        Directory.CreateDirectory(Path.Combine(_root, "movies", "live-action", "Heat (1995)"));
        Directory.CreateDirectory(Path.Combine(_root, "tv"));
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private string Heat => Path.Combine(_root, "movies", "live-action", "Heat (1995)");

    private string File(string relative, int bytes = 5000)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[bytes];
        new Random(bytes).NextBytes(data);
        System.IO.File.WriteAllBytes(path, data);
        return path;
    }

    private ScratcharrOptions Options(bool allowChanges = true) => new ScratcharrOptions
    {
        DownloadsRoot = Path.Combine(_root, "downloads"),
        UsbRoot = Path.Combine(_root, "pc"),
        MoviesLibrary = Path.Combine(_root, "movies"),
        TvLibrary = Path.Combine(_root, "tv"),
        AllowChanges = allowChanges
    }.WithDefaults();

    private (JobQueue Queue, Store Store) Make(ScratcharrOptions options)
    {
        var store = new Store(Path.Combine(_root, "test.db"));
        var live = new LiveSettings(MsOptions.Create(options), store);
        var scans = new ScanService(MsOptions.Create(options), store, live,
            new QbittorrentClient(live, NullLogger<QbittorrentClient>.Instance),
            new SabnzbdClient(live, NullLogger<SabnzbdClient>.Instance),
            new ArrClient(live, NullLogger<ArrClient>.Instance),
            NullLogger<ScanService>.Instance);
        return (new JobQueue(MsOptions.Create(options), store, scans, NullLogger<JobQueue>.Instance), store);
    }

    private static async Task RunAll(JobQueue queue)
    {
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
        if (queue.Jobs().Any(j => j.Open))
        {
            await finished.Task.WaitAsync(stop.Token);
        }

        await queue.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Copies_the_ticked_videos_into_their_extras_folders()
    {
        var (queue, store) = Make(Options());
        var making = File("downloads/torrents/manual/Heat.1995.Extras/Making.Of.mkv");
        var trailer = File("downloads/torrents/manual/Heat.1995.Extras/Teaser.mkv", 3000);
        var source = Path.GetDirectoryName(making)!;

        var problem = queue.QueueExtras(source, Heat,
        [
            new ExtraFile(making, ExtraTypes.Guess(making) ?? ExtraTypes.Default, FileNames.ExtraName(making)),
            new ExtraFile(trailer, ExtraTypes.Guess(trailer)!, "Teaser")
        ]);
        Assert.IsNull(problem);
        await RunAll(queue);

        var job = queue.Jobs().Single();
        Assert.AreEqual(JobState.Done, job.State, job.Message);
        Assert.AreEqual(JobKind.Extras, job.Kind);
        StringAssert.StartsWith(job.Message, "Copied 2 extras.");
        CollectionAssert.AreEqual(System.IO.File.ReadAllBytes(making),
            System.IO.File.ReadAllBytes(Path.Combine(Heat, "behind the scenes", "Making Of.mkv")));
        Assert.IsTrue(System.IO.File.Exists(Path.Combine(Heat, "trailers", "Teaser.mkv")));
        Assert.IsTrue(System.IO.File.Exists(making), "the download stays (it may be seeding)");
        Assert.IsNull(store.Setting(ExtrasJob.Key(job.Id)), "the list is forgotten once done");

        // Again: everything is there, nothing is queued.
        Assert.AreEqual("They are all in the library already.",
            queue.QueueExtras(source, Heat, [new ExtraFile(trailer, "trailers", "Teaser")]));
    }

    [TestMethod]
    public void Refuses_what_it_must_not_do()
    {
        var video = File("downloads/import/Extra.mkv");
        var (readOnly, _) = Make(Options(allowChanges: false));
        StringAssert.StartsWith(readOnly.QueueExtras(video, Heat, [new ExtraFile(video, "featurettes", "Extra")]), "Changes are off");

        var (queue, _) = Make(Options());
        var outside = File("elsewhere/Extra.mkv");
        StringAssert.StartsWith(queue.QueueExtras(outside, Heat, [new ExtraFile(outside, "featurettes", "Extra")]), "Extras are copied only from");
        StringAssert.Contains(queue.QueueExtras(video, Path.Combine(_root, "movies", "live-action"), [new ExtraFile(video, "featurettes", "Extra")]),
            "is not a film or series folder");
        StringAssert.Contains(queue.QueueExtras(video, Heat, [new ExtraFile(video, "clips", "Extra")]), "not an extras folder");
        Assert.AreEqual("Tick at least one video.", queue.QueueExtras(video, Heat, []));
        Assert.IsEmpty(queue.Jobs());
    }
}
