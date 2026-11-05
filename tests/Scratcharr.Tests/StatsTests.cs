using Microsoft.Extensions.Logging.Abstractions;
using Scratcharr.Clean;
using Scratcharr.Jobs;
using Scratcharr.Options;
using Scratcharr.Scan;
using Scratcharr.Tools;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Scratcharr.Tests;

[TestClass]
public sealed class StatsTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private const long Gb = 1L << 30;

    private static Job Job(string name, JobState state, JobKind kind = JobKind.Copy, int finishedHoursAgo = 1) => new()
    {
        Kind = kind, Source = "/mnt/pc/usb01/" + name, Target = "/downloads/import/" + name, State = state,
        Finished = state is JobState.Done or JobState.Failed ? Now.AddHours(-finishedHoursAgo) : null
    };

    [TestMethod]
    public void The_dashboard_gets_the_jobs_the_space_and_what_to_review_in_one_line_each()
    {
        var tools = new ToolStatusService(MsOptions.Create(new ScratcharrOptions().WithDefaults()), NullLogger<ToolStatusService>.Instance);
        var scan = new ScanResult { FreeBytes = 50 * Gb, TotalBytes = 1000 * Gb };
        var jobs = new[]
        {
            Job("Big.Show", JobState.Running), Job("Next", JobState.Queued), Job("Later", JobState.Queued),
            Job("Broken", JobState.Failed), Job("Fine", JobState.Done), Job("Old", JobState.Failed, finishedHoursAgo: 30)
        };

        var stats = Stats.From(scan, false, tools, 3, [], jobs, 42, new SpaceAlertSettings(), Now);
        Assert.AreEqual("Copying Big.Show · 42%, 2 queued, 1 failed today", stats.JobStatus);
        Assert.AreEqual((2, 1, 1, 1), (stats.JobsQueued, stats.JobsRunning, stats.JobsDone, stats.JobsFailed));
        Assert.AreEqual("Low: 50.0 GB free (5%)", stats.Space);
        Assert.AreEqual(5.0, stats.FreePercent);
        Assert.AreEqual("Nothing", stats.Review);

        var idle = Stats.From(scan with { FreeBytes = 500 * Gb }, false, tools, 0, [], [Job("Fine", JobState.Done)], 0, null, Now);
        Assert.AreEqual("Idle, 1 done today", idle.JobStatus);
        Assert.AreEqual("500 GB free (50%)", idle.Space);
        Assert.AreEqual("Idle", Stats.From(null, false, tools).JobStatus);

        var timed = Stats.From(scan, false, tools, 1, [], [Job("Disc", JobState.Running, JobKind.Unpack)], 42, null, Now, "about 4 min left · 210 MB/s");
        Assert.AreEqual("Unpacking Disc · 42%, about 4 min left", timed.JobStatus);
    }
}
