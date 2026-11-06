using System.Threading.Channels;
using Arrkit.Library;
using Microsoft.Extensions.Options;
using Scratcharr.Options;
using Scratcharr.Scan;
using Scratcharr.Unpack;
using Scratcharr.Usb;

namespace Scratcharr.Jobs;

/// <summary>
/// Runs jobs one at a time: unpacks (7-Zip) and copies from USB. Each writes into "&lt;target&gt;.unpacking" or
/// "&lt;target&gt;.copying", which gets its real name only when everything is there, so Ripparr and the arr apps
/// never see half a disc or half a file. Archives and USB disks are only read. Pause all holds the queue (see
/// <see cref="PauseGate"/>).
/// </summary>
public sealed class JobQueue(IOptions<ScratcharrOptions> options, Store store, ScanService scans, ILogger<JobQueue> logger)
    : BackgroundService
{
    private const int LogLines = 300;

    /// <summary>The settings key holding "1" while the queue is paused.</summary>
    private const string PausedKey = "jobs.paused";

    private readonly ScratcharrOptions _options = options.Value;
    private readonly Channel<long> _queue = Channel.CreateUnbounded<long>();
    private readonly Dictionary<long, CancellationTokenSource> _running = [];
    private readonly Lock _lock = new();
    private readonly PauseGate _pause = new(store.Setting(PausedKey) == "1");

    /// <summary>Extras jobs stopped by Pause all: they go back to the queue instead of being cancelled.</summary>
    private readonly HashSet<long> _requeue = [];

    private JobKind? _runningKind;

    /// <summary>Pause all is on: no job starts, the running one waits where it is.</summary>
    public bool Paused => _pause.Paused;

    /// <summary>Percent done of the running job.</summary>
    public int Progress { get; private set; }

    public long? RunningId { get; private set; }

    /// <summary>Time left and speed of the running job.</summary>
    public Eta Eta { get; } = new();

    /// <summary>Bytes the running job's 0-100% stands for (a verified copy reads everything back, so twice its size).</summary>
    private long _workBytes;

    /// <summary>"about 4 min left · 210 MB/s" for the running job; empty until there is enough to go on.</summary>
    public string Remaining(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (RunningId != null && Paused)
        {
            return "Paused";
        }

        if (RunningId == null || Eta.Left(now) is not { } left)
        {
            return "";
        }

        return Eta.Speed(_workBytes, now) is { } speed and > 0
            ? $"{Eta.Text(left)} · {Eta.SpeedText(speed)}"
            : Eta.Text(left);
    }

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

    /// <summary>Queues a copy from a USB disk after the checks; returns the problem instead when one stops it.</summary>
    public string? QueueCopy(string source, CopyDestination destination)
    {
        source = Paths.Clean(source);
        var kind = CopyPlan.KindOf(source);
        var target = CopyPlan.Target(_options, source, kind, destination);
        var (size, _) = File.Exists(source) || Directory.Exists(source) ? CopyPlan.Measure(source) : (0, 0);
        var problem = CopyPlan.Problem(_options, source, target, size, scans.Last?.FreeBytes ?? 0, store.Jobs(0));
        if (problem != null)
        {
            return problem;
        }

        var id = store.AddJob(new Job
        {
            Kind = JobKind.Copy,
            Source = source,
            Target = target,
            State = JobState.Queued,
            Created = DateTime.UtcNow,
            Size = size
        });
        logger.LogInformation("Queued copy {Id}: {Source} -> {Target}", id, source, target);
        _queue.Writer.TryWrite(id);
        Changed?.Invoke();
        return null;
    }

    /// <summary>The plan for copying videos as extras into a film's or series' folder (reads the disk only).</summary>
    public List<ExtraStep> PlanExtras(string titleFolder, IEnumerable<ExtraFile> files) =>
        ExtrasPlan.Make(_options.LibraryRoots, titleFolder, files);

    /// <summary>Queues copying videos as extras into a library folder; returns the problem instead when one stops it.</summary>
    public string? QueueExtras(string source, string titleFolder, IReadOnlyList<ExtraFile> files)
    {
        source = Paths.Clean(source);
        titleFolder = Paths.Clean(titleFolder);
        var steps = PlanExtras(titleFolder, files);
        var problem = ExtrasJob.Problem(_options, source, titleFolder, steps, store.Jobs(0));
        if (problem != null)
        {
            return problem;
        }

        var id = store.AddJob(new Job
        {
            Kind = JobKind.Extras,
            Source = source,
            Target = titleFolder,
            State = JobState.Queued,
            Created = DateTime.UtcNow,
            Size = steps.Where(s => s.ToCopy).Sum(s => s.Size)
        });
        store.SetSetting(ExtrasJob.Key(id), ExtrasJob.Save(files));
        logger.LogInformation("Queued extras {Id}: {Count} from {Source} -> {Target}", id, files.Count, source, titleFolder);
        _queue.Writer.TryWrite(id);
        Changed?.Invoke();
        return null;
    }

    /// <summary>
    /// Holds the queue: no job starts, a running copy waits before its next block and a running unpack's 7-Zip is
    /// stopped, both carrying on where they were at Resume all. A running extras copy stops and goes back to the queue
    /// (the extras already in the library are skipped when it carries on). Saved, so it stays paused after a restart.
    /// </summary>
    public void PauseAll()
    {
        if (Paused)
        {
            return;
        }

        store.SetSetting(PausedKey, "1");
        _pause.Pause();
        lock (_lock)
        {
            if (RunningId is { } id && _runningKind == JobKind.Extras && _running.TryGetValue(id, out var source))
            {
                _requeue.Add(id);
                source.Cancel();
            }
        }

        logger.LogInformation("Paused all jobs");
        Changed?.Invoke();
    }

    /// <summary>Lets the running job carry on and the queued ones start again, oldest first.</summary>
    public void ResumeAll()
    {
        if (!Paused)
        {
            return;
        }

        store.SetSetting(PausedKey, null);
        Eta.Reset(); // the pause would make it look slower than it is
        _pause.Resume();
        logger.LogInformation("Resumed all jobs");
        _queue.Writer.TryWrite(0);
        Changed?.Invoke();
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
            // A copy waiting to carry on after a restart has a partial copy: it goes too.
            Clean(job.Partial);
            ForgetCopied(job.Id);
            ForgetExtras(job.Id);
            store.UpdateJob(job with { State = JobState.Cancelled, Finished = DateTime.UtcNow, Message = "Cancelled before it started." });
            Changed?.Invoke();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A job still marked running was cut short (the container was killed): a copy carries on from the next file it
        // had not finished, an unpack starts again (7-Zip can't resume). Queued ones are picked up again.
        foreach (var job in store.Jobs(0))
        {
            if (job.State == JobState.Running)
            {
                store.UpdateJob(job with { State = JobState.Queued, Message = Interrupted(job) });
                if (job.Kind == JobKind.Unpack)
                {
                    Clean(job.Partial);
                }
            }

        }

        _queue.Writer.TryWrite(0);

        // Each message only wakes the loop: it then runs every queued job, oldest first, waiting while paused.
        await foreach (var _ in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();
                await _pause.WaitAsync(stoppingToken);
                if (store.Jobs(0).FirstOrDefault(j => j.State == JobState.Queued) is not { } job)
                {
                    break;
                }

                await RunAsync(job, stoppingToken);
            }
        }
    }

    private async Task RunAsync(Job job, CancellationToken stoppingToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_lock)
        {
            _running[job.Id] = cancel;
            _runningKind = job.Kind;
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

        var partial = job.Partial;
        job = job with { State = JobState.Running, Started = DateTime.UtcNow };
        store.UpdateJob(job);
        RunningId = job.Id;
        Progress = 0;
        Eta.Reset();
        _workBytes = job.Kind is JobKind.Copy or JobKind.Extras && _options.VerifyCopies ? job.Size * 2 : job.Size;
        _lastSent = DateTime.MinValue;
        Changed?.Invoke();

        try
        {
            job = job.Kind switch
            {
                JobKind.Copy => await CopyAsync(job, partial, Log, cancel.Token),
                JobKind.Extras => await ExtrasAsync(job, Log, cancel.Token),
                _ => await UnpackAsync(job, partial, log, Log, cancel.Token)
            };
            if (job.State == JobState.Done)
            {
                Progress = 100;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Scratcharr is stopping (container restart): the job waits in the queue and carries on at the next start.
            if (job.Kind == JobKind.Unpack)
            {
                Clean(partial);
            }

            Log("Scratcharr stopped.");
            job = job with { State = JobState.Queued, Message = Interrupted(job) };
        }
        catch (OperationCanceledException) when (Requeued(job.Id))
        {
            Log("Paused.");
            job = job with
            {
                State = JobState.Queued,
                Message = "Paused: it carries on at Resume all, skipping the extras already in the library."
            };
        }
        catch (OperationCanceledException)
        {
            Clean(partial);
            ForgetCopied(job.Id);
            ForgetExtras(job.Id);
            job = job with
            {
                State = JobState.Cancelled,
                Message = job.Kind == JobKind.Extras ? "Cancelled. The extras copied before it stay in the library." : "Cancelled."
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log(e.Message);
            Clean(partial);
            ForgetCopied(job.Id);
            ForgetExtras(job.Id);
            job = job with
            {
                State = JobState.Failed,
                Message = job.Kind switch
                {
                    JobKind.Copy => CopyReason(e),
                    JobKind.Extras => ExtrasCopier.Reason(e),
                    _ => SevenZip.Reason(1, e.Message)
                }
            };
        }
        finally
        {
            lock (_lock)
            {
                _running.Remove(job.Id);
                _requeue.Remove(job.Id);
                _runningKind = null;
            }

            // A job back in the queue (Scratcharr stopping, Pause all) has not finished.
            job = job with { Finished = job.Open ? null : DateTime.UtcNow, Log = string.Join('\n', log) };
            store.UpdateJob(job);
            logger.LogInformation("{Kind} {Id} {State}: {Message}", job.Kind, job.Id, job.State, job.Message);
            RunningId = null;
            Changed?.Invoke();
            if (job.State == JobState.Done)
            {
                scans.ScanNow();
            }
        }
    }

    private bool Requeued(long id)
    {
        lock (_lock)
        {
            return _requeue.Contains(id);
        }
    }

    private DateTime _lastSent = DateTime.MinValue;

    /// <summary>Sets the running job's percent, telling the pages at most twice a second.</summary>
    private void Report(int percent)
    {
        if (percent != Progress && (DateTime.UtcNow - _lastSent).TotalMilliseconds > 500)
        {
            Progress = percent;
            _lastSent = DateTime.UtcNow;
            Eta.Add(percent, _lastSent);
            Changed?.Invoke();
        }
    }

    private async Task<Job> UnpackAsync(Job job, string partial, Queue<string> log, Action<string> Log, CancellationToken ct)
    {
        // Checked again: things may have changed while it waited in the queue.
        var problem = UnpackPlan.Problem(_options, job.Source, job.Target, job.Size, scans.Last?.FreeBytes ?? 0,
            store.Jobs(0).Where(j => j.Id != job.Id));
        if (problem != null)
        {
            return job with { State = JobState.Failed, Message = problem };
        }

        Directory.CreateDirectory(_options.DiscsFolder);
        Clean(partial);
        Log($"7-Zip: {job.Source} -> {partial}");
        var result = await SevenZip.ExtractAsync(_options.Tools.SevenZip, job.Source, partial, Report, Log, ct, _pause);
        if (result.ExitCode != 0)
        {
            Log(result.Error.Trim());
            Clean(partial);
            return job with { State = JobState.Failed, Message = SevenZip.Reason(result.ExitCode, result.Error + "\n" + string.Join('\n', log)) };
        }

        Directory.Move(partial, job.Target);
        var discs = UnpackPlan.Discs(job.Target);
        return job with
        {
            State = JobState.Done,
            Message = discs.Count switch
            {
                0 => "Unpacked. No ISO or BDMV folder inside.",
                1 => "Unpacked: 1 disc for Ripparr.",
                _ => $"Unpacked: {discs.Count} discs for Ripparr."
            }
        };
    }

    private async Task<Job> CopyAsync(Job job, string partial, Action<string> Log, CancellationToken ct)
    {
        var problem = CopyPlan.Problem(_options, job.Source, job.Target, job.Size, scans.Last?.FreeBytes ?? 0,
            store.Jobs(0).Where(j => j.Id != job.Id));
        if (problem != null)
        {
            return job with { State = JobState.Failed, Message = problem };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(job.Target)!);
        var copied = Copied(job.Id);
        if (copied.Count == 0)
        {
            Clean(partial);
        }

        Log($"Copy: {job.Source} -> {partial}{(_options.VerifyCopies ? ", then read back and compared" : "")}"
            + (copied.Count > 0 ? $", carrying on after a restart ({copied.Count} files done)" : ""));
        var list = new System.Text.StringBuilder(store.Setting(CopiedKey(job.Id)) ?? "");
        var result = await UsbCopy.CopyAsync(job.Source, partial, _options.VerifyCopies,
            (done, total) => Report(total == 0 ? 100 : (int)(done * 100 / total)), Log, ct, copied,
            file =>
            {
                list.Append(file.Line).Append('\n');
                store.SetSetting(CopiedKey(job.Id), list.ToString());
            }, _pause);
        ForgetCopied(job.Id);

        if (Directory.Exists(partial))
        {
            Directory.Move(partial, job.Target);
        }
        else
        {
            File.Move(partial, job.Target);
        }

        var what = $"{(result.Files == 1 ? "1 file" : $"{result.Files} files")}, {Format.Size(result.Bytes)}";
        var discs = Paths.IsUnder(job.Target, _options.DiscsFolder) ? UnpackPlan.Discs(job.Target).Count : 0;
        var where = discs switch
        {
            0 when Paths.IsUnder(job.Target, _options.ImportFolder) => " Ready for Manual Import in Radarr or Sonarr.",
            0 => "",
            1 => " 1 disc for Ripparr.",
            _ => $" {discs} discs for Ripparr."
        };
        return job with
        {
            State = JobState.Done,
            Size = result.Bytes,
            Message = $"Copied {what}{(result.Verified ? ", every file checked" : "")}"
                      + (result.Resumed > 0 ? $", carried on after a restart ({result.Resumed} files kept)" : "") + $".{where}"
        };
    }

    private async Task<Job> ExtrasAsync(Job job, Action<string> Log, CancellationToken ct)
    {
        var files = ExtrasJob.Load(store.Setting(ExtrasJob.Key(job.Id)));
        if (files.Count == 0)
        {
            return job with { State = JobState.Failed, Message = "The list of extras was lost: queue them again." };
        }

        // Planned again: the library may have changed while it waited. What is there already (also after a restart) is skipped.
        var steps = PlanExtras(job.Target, files);
        if (steps.FirstOrDefault(s => s.State == ExtraStepState.Problem) is { } bad)
        {
            ForgetExtras(job.Id);
            return job with { State = JobState.Failed, Message = $"{Path.GetFileName(bad.File.Source)}: {bad.Problem}" };
        }

        _workBytes = steps.Where(s => s.ToCopy).Sum(s => s.Size) * (_options.VerifyCopies ? 2 : 1);
        Log($"Extras: {files.Count} into {job.Target}{(_options.VerifyCopies ? ", each read back and compared" : "")}");
        var result = await ExtrasCopier.CopyAsync(steps, _options.VerifyCopies,
            (done, total) => Report(total == 0 ? 100 : (int)(done * 100 / total)), Log, ct);
        ForgetExtras(job.Id);
        foreach (var (step, target) in result.Placed)
        {
            Log($"{Path.GetFileName(step.File.Source)} -> {Path.GetRelativePath(job.Target, target)}");
        }

        return job with
        {
            State = result.Ok ? JobState.Done : JobState.Failed,
            Size = result.Bytes,
            Message = result.Summary + (result.Ok && result.Copied > 0 ? " Jellyfin shows them after its next scan of the folder." : "")
        };
    }

    private void ForgetExtras(long id)
    {
        if (store.Setting(ExtrasJob.Key(id)) != null)
        {
            store.SetSetting(ExtrasJob.Key(id), null);
        }
    }

    /// <summary>A failed copy in plain words.</summary>
    private static string CopyReason(Exception e) => e.Message switch
    {
        var m when m.Contains("No space left", StringComparison.OrdinalIgnoreCase) => "The downloads pool is full.",
        var m when m.Contains("Read-only file system", StringComparison.OrdinalIgnoreCase) =>
            "/downloads is mounted read-only. Mount it read-write (without :ro) to copy.",
        var m when m.Contains("Input/output error", StringComparison.OrdinalIgnoreCase) =>
            "The external disk or share gave a read error (Input/output error). Check the cable, the disk or the share.",
        _ => e.Message
    };

    private static string Interrupted(Job job) => job.Kind switch
    {
        JobKind.Copy => "Scratcharr stopped while it ran: the copy carries on from the files it had not finished.",
        JobKind.Extras => "Scratcharr stopped while it ran: the extras already in the library are skipped, the rest are copied.",
        _ => "Scratcharr stopped while it ran: the unpack starts again."
    };

    /// <summary>The settings key holding the files a copy job finished, one <see cref="CopiedFile.Line"/> each.</summary>
    private static string CopiedKey(long id) => $"job.{id}.copied";

    private IReadOnlyList<CopiedFile> Copied(long id) =>
        (store.Setting(CopiedKey(id)) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(CopiedFile.Parse).OfType<CopiedFile>().ToList();

    private void ForgetCopied(long id)
    {
        if (store.Setting(CopiedKey(id)) != null)
        {
            store.SetSetting(CopiedKey(id), null);
        }
    }

    /// <summary>Removes a half-written ".unpacking" or ".copying" folder or file: only ever Scratcharr's own output.</summary>
    private void Clean(string partial)
    {
        if (!(partial.EndsWith(UnpackPlan.PartialSuffix, StringComparison.Ordinal) || partial.EndsWith(CopyPlan.PartialSuffix, StringComparison.Ordinal))
            || !(Paths.IsUnder(partial, _options.DiscsFolder) || Paths.IsUnder(partial, _options.ImportFolder)))
        {
            return;
        }

        try
        {
            if (Directory.Exists(partial))
            {
                Directory.Delete(partial, recursive: true);
            }
            else if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove {Partial}: {Message}", partial, e.Message);
        }
    }
}
