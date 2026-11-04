namespace Scratcharr.Jobs;

public enum JobKind
{
    Unpack
}

public enum JobState
{
    Queued,
    Running,
    Done,
    Failed,
    Cancelled
}

/// <summary>A queued or finished piece of work that changes files: for now, unpacking an archive set.</summary>
public sealed record Job
{
    public long Id { get; init; }
    public JobKind Kind { get; init; }

    /// <summary>The file 7-Zip opens (the first part).</summary>
    public required string Source { get; init; }

    /// <summary>The folder the files go to, under /downloads/discs.</summary>
    public required string Target { get; init; }

    /// <summary>The scanned item the archive was found in.</summary>
    public string Item { get; init; } = "";

    public JobState State { get; init; }
    public DateTime Created { get; init; }
    public DateTime? Started { get; init; }
    public DateTime? Finished { get; init; }

    /// <summary>Unpacked size in bytes, from the listing (0 when unknown).</summary>
    public long Size { get; init; }

    public string Message { get; init; } = "";
    public string Log { get; init; } = "";

    public bool Open => State is JobState.Queued or JobState.Running;

    public string Name => Path.GetFileName(Target);
}

public static class JobStateText
{
    public static string Css(this JobState state) => state switch
    {
        JobState.Queued => "secondary",
        JobState.Running => "primary",
        JobState.Done => "success",
        JobState.Failed => "danger",
        _ => "warning"
    };
}
