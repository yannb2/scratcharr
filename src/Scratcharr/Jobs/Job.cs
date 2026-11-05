namespace Scratcharr.Jobs;

public enum JobKind
{
    Unpack,
    Copy,

    /// <summary>Videos copied into a film's or series' folder in the library as extras (featurettes/, trailers/...).</summary>
    Extras
}

public enum JobState
{
    Queued,
    Running,
    Done,
    Failed,
    Cancelled
}

/// <summary>A queued or finished piece of work that writes files: unpacking an archive set, copying from a USB disk, or copying extras into the library.</summary>
public sealed record Job
{
    public long Id { get; init; }
    public JobKind Kind { get; init; }

    /// <summary>Unpack: the file 7-Zip opens (the first part). Copy: the file or folder on the USB disk. Extras: the file or folder they come from.</summary>
    public required string Source { get; init; }

    /// <summary>Where the files go: a folder under /downloads/discs, a file or folder under /downloads/import, or (extras) the film's or series' folder.</summary>
    public required string Target { get; init; }

    /// <summary>"&lt;target&gt;.unpacking" or "&lt;target&gt;.copying": written first, renamed when complete.</summary>
    public string Partial => Kind switch
    {
        JobKind.Copy => Target + Usb.CopyPlan.PartialSuffix,
        JobKind.Unpack => Target + UnpackPlan.PartialSuffix,
        // Each extra has its own hidden .copying file, which the copy removes itself.
        _ => ""
    };

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
