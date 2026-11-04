using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Scratcharr.Options;
using Scratcharr.Scan;
using Scratcharr.Unpack;

namespace Scratcharr.Jobs;

/// <summary>
/// Runs unpack jobs one at a time. 7-Zip writes into "&lt;target&gt;.unpacking" and the folder gets its real
/// name only when everything is out, so Ripparr never sees half a disc. The archive itself is only read.
/// </summary>
public sealed class JobQueue(IOptions<ScratcharrOptions> options, Store store, ScanService scans, ILogger<JobQueue> logger)
    : BackgroundService
{
    private const int LogLines = 300;

    private readonly ScratcharrOptions _options = options.Value;
    private readonly Channel<long> _queue = Channel.CreateUnbounded<long>();
    private readonly Dictionary<long, CancellationTokenSource> _running = [];
    private readonly Lock _lock = new();

    /// <summary>Percent done of the running job.</summary>
    public int Progress { get; private set; }

    public long? RunningId { get; private set; }

    public event Action? Changed;

    public IReadOnlyList<Job> Jobs() => store.Jobs();

    public int OpenCount => store.Jobs(0).Count;

    /// <summary>Queues an unpack after the checks; returns the problem instead when one stops it.</summary>
    public string? QueueUnpack(ArchiveSet set, string name, ArchiveListing listing)
    {
        var target = UnpackPlan.Target(_options, name);
        var problem = listing.Error ?? UnpackPlan.Problem(_options, set.Start, target, listing.UnpackedSize, scans.Last?.FreeBytes ?? 0, store.Jobs(0));
        if (problem != null)
        {
            return problem;
        }

        var id = store.AddJob(new Job
        {
            Kind = JobKind.Unpack,
            Source = set.Start,
            Target = target!,
            Item = set.ItemPath,
            State = JobState.Queued,
            Created = DateTime.UtcNow,
            Size = listing.UnpackedSize
        });
        logger.LogInformation("Queued unpack {Id}: {Source} -> {Target}", id, set.Start, target);
        _queue.Writer.TryWrite(id);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Stops a running job, or takes a queued one out of the queue.</summary>
    public void Cancel(long id)
    {
        lock (_lock)
        {
            if (_running.TryGetValue(id, out var source))
            {
                source.Cancel();
                return;
            }
        }

        if (store.Job(id) is { State: JobState.Queued } job)
        {
            store.UpdateJob(job with { State = JobState.Cancelled, Finished = DateTime.UtcNow, Message = "Cancelled before it started." });
            Changed?.Invoke();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A job that was running when the app stopped did not finish; queued ones are picked up again.
        foreach (var job in store.Jobs(0))
        {
            if (job.State == JobState.Running)
            {
                Clean(job.Target + UnpackPlan.PartialSuffix);
                store.UpdateJob(job with { State = JobState.Failed, Finished = DateTime.UtcNow, Message = "Scratcharr restarted while it ran." });
            }
            else
            {
                _queue.Writer.TryWrite(job.Id);
            }
        }

        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (store.Job(id) is not { State: JobState.Queued } job)
            {
                continue;
            }

            await RunAsync(job, stoppingToken);
        }
    }

    private async Task RunAsync(Job job, CancellationToken stoppingToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_lock)
        {
            _running[job.Id] = cancel;
        }

        var log = new Queue<string>();
        void Log(string line)
        {
            log.Enqueue(line);
            while (log.Count > LogLines)
            {
                log.Dequeue();
            }
        }

        var partial = job.Target + UnpackPlan.PartialSuffix;
        job = job with { State = JobState.Running, Started = DateTime.UtcNow };
        store.UpdateJob(job);
        RunningId = job.Id;
        Progress = 0;
        Changed?.Invoke();

        try
        {
            // Checked again: things may have changed while it waited in the queue.
            var problem = UnpackPlan.Problem(_options, job.Source, job.Target, job.Size, scans.Last?.FreeBytes ?? 0,
                store.Jobs(0).Where(j => j.Id != job.Id));
            if (problem != null)
            {
                job = job with { State = JobState.Failed, Message = problem };
                return;
            }

            Directory.CreateDirectory(_options.DiscsFolder);
            Clean(partial);
            Log($"7-Zip: {job.Source} -> {partial}");
            var lastSent = DateTime.MinValue;
            var result = await SevenZip.ExtractAsync(_options.Tools.SevenZip, job.Source, partial, percent =>
            {
                if (percent != Progress && (DateTime.UtcNow - lastSent).TotalMilliseconds > 500)
                {
                    Progress = percent;
                    lastSent = DateTime.UtcNow;
                    Changed?.Invoke();
                }
            }, Log, cancel.Token);

            if (result.ExitCode != 0)
            {
                Log(result.Error.Trim());
                Clean(partial);
                job = job with { State = JobState.Failed, Message = SevenZip.Reason(result.ExitCode, result.Error + "\n" + string.Join('\n', log)) };
                return;
            }

            Directory.Move(partial, job.Target);
            var discs = UnpackPlan.Discs(job.Target);
            job = job with
            {
                State = JobState.Done,
                Message = discs.Count switch
                {
                    0 => "Unpacked. No ISO or BDMV folder inside.",
                    1 => "Unpacked: 1 disc for Ripparr.",
                    _ => $"Unpacked: {discs.Count} discs for Ripparr."
                }
            };
            Progress = 100;
        }
        catch (OperationCanceledException)
        {
            Clean(partial);
            job = job with { State = JobState.Cancelled, Message = stoppingToken.IsCancellationRequested ? "Scratcharr stopped." : "Cancelled." };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log(e.Message);
            Clean(partial);
            job = job with { State = JobState.Failed, Message = SevenZip.Reason(1, e.Message) };
        }
        finally
        {
            lock (_lock)
            {
                _running.Remove(job.Id);
            }

            job = job with { Finished = DateTime.UtcNow, Log = string.Join('\n', log) };
            store.UpdateJob(job);
            logger.LogInformation("Unpack {Id} {State}: {Message}", job.Id, job.State, job.Message);
            RunningId = null;
            Changed?.Invoke();
            if (job.State == JobState.Done)
            {
                scans.ScanNow();
            }
        }
    }

    /// <summary>Removes a half-written ".unpacking" folder: only ever Scratcharr's own output.</summary>
    private void Clean(string partial)
    {
        if (!partial.EndsWith(UnpackPlan.PartialSuffix, StringComparison.Ordinal) || !Paths.IsUnder(partial, _options.DiscsFolder))
        {
            return;
        }

        try
        {
            if (Directory.Exists(partial))
            {
                Directory.Delete(partial, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove {Partial}: {Message}", partial, e.Message);
        }
    }
}
