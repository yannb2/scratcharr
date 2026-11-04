namespace Scratcharr.Jobs;

public enum JobKind
{
    Unpack,
    Copy
}

public enum JobState
{
    Queued,
    Running,
    Done,
    Failed,
    Cancelled
}

/// <summary>A queued or finished piece of work that writes files: unpacking an archive set, or copying from a USB disk.</summary>
public sealed record Job
{
    public long Id { get; init; }
    public JobKind Kind { get; init; }

    /// <summary>Unpack: the file 7-Zip opens (the first part). Copy: the file or folder on the USB disk.</summary>
    public required string Source { get; init; }

    /// <summary>Where the files go: a folder under /downloads/discs, or a file or folder under /downloads/import.</summary>
    public required string Target { get; init; }

    /// <summary>"&lt;target&gt;.unpacking" or "&lt;target&gt;.copying": written first, renamed when complete.</summary>
    public string Partial => Target + (Kind == JobKind.Copy ? Usb.CopyPlan.PartialSuffix : UnpackPlan.PartialSuffix);

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
