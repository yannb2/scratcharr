namespace Scratcharr.Clients;

/// <summary>One torrent as qBittorrent reports it.</summary>
public sealed record Torrent
{
    /// <summary>The info hash, upper case: what the arr apps call the download ID.</summary>
    public required string Hash { get; init; }
    public required string Name { get; init; }
    public string Category { get; init; } = "";
    public string State { get; init; } = "";

    /// <summary>The torrent's file (single-file torrent) or root folder.</summary>
    public string ContentPath { get; init; } = "";
    public string SavePath { get; init; } = "";
    public long Size { get; init; }
    public double Progress { get; init; }
    public long SeedingSeconds { get; init; }

    /// <summary>Minutes; -2 = the global limit, -1 = no limit.</summary>
    public long SeedingLimitMinutes { get; init; } = -2;

    public double Ratio { get; init; }

    /// <summary>-2 = the global limit, -1 = no limit.</summary>
    public double RatioLimit { get; init; } = -2;

    public DateTime? Added { get; init; }
    public DateTime? Completed { get; init; }

    public bool Finished => Progress >= 1;

    /// <summary>Stopped (qBittorrent 5) or paused (4.x): no longer seeding.</summary>
    public bool Stopped => State.StartsWith("paused", StringComparison.Ordinal) || State.StartsWith("stopped", StringComparison.Ordinal);

    public bool Errored => State is "error" or "missingFiles";

    /// <summary>
    /// The seeding time limit that applies, in minutes: the torrent's own, else qBittorrent's global one. Null when
    /// there is none (-1 = no limit, or no global limit set).
    /// </summary>
    public long? LimitMinutes(SeedingLimits global) => SeedingLimitMinutes switch
    {
        -2 => global.Minutes,
        < 0 => null,
        var m => m
    };

    /// <summary>A short word for the state: Downloading, Seeding, Stopped...</summary>
    public string StateWord => State switch
    {
        "downloading" or "forcedDL" or "metaDL" or "forcedMetaDL" or "stalledDL" => "Downloading",
        "uploading" or "forcedUP" or "stalledUP" => "Seeding",
        "queuedDL" => "Queued",
        "queuedUP" => "Queued to seed",
        "pausedDL" or "stoppedDL" => "Stopped, unfinished",
        "pausedUP" or "stoppedUP" => "Stopped",
        "checkingDL" or "checkingUP" or "checkingResumeData" => "Checking",
        "moving" => "Moving",
        "missingFiles" => "Files missing",
        "error" => "Error",
        "" => "",
        _ => State
    };
}

/// <summary>What qBittorrent does when a torrent reaches its seeding time or ratio limit.</summary>
public enum ShareLimitAction
{
    Stop = 0,
    Remove = 1,
    SuperSeeding = 2,
    RemoveWithFiles = 3
}

/// <summary>The global seeding limits (Options > BitTorrent > Seeding Limits).</summary>
public sealed record SeedingLimits
{
    /// <summary>Minutes, or null when off.</summary>
    public long? Minutes { get; init; }

    /// <summary>Null when off.</summary>
    public double? Ratio { get; init; }

    public ShareLimitAction Action { get; init; }
}

public sealed record QbittorrentState
{
    public string Version { get; init; } = "";
    public IReadOnlyList<Torrent> Torrents { get; init; } = [];

    /// <summary>Category name to its save path (empty = the default save path + the name).</summary>
    public IReadOnlyDictionary<string, string> Categories { get; init; } = new Dictionary<string, string>();

    public SeedingLimits Limits { get; init; } = new();
    public string DefaultSavePath { get; init; } = "";
}

/// <summary>One SABnzbd job, from the queue or the history.</summary>
public sealed record SabJob
{
    /// <summary>SABnzbd_nzo_...: what the arr apps call the download ID.</summary>
    public required string NzoId { get; init; }
    public required string Name { get; init; }
    public string Category { get; init; } = "";

    /// <summary>Queue: Downloading, Queued, Paused... History: Completed, Failed, Extracting...</summary>
    public string Status { get; init; } = "";

    public bool InQueue { get; init; }

    /// <summary>Where the files are: the final folder for a finished job, the temporary one otherwise.</summary>
    public string Path { get; init; } = "";

    /// <summary>The temporary folder of a history entry (left behind when post-processing failed).</summary>
    public string IncompletePath { get; init; } = "";

    public long Bytes { get; init; }
    public DateTime? Completed { get; init; }
    public string FailMessage { get; init; } = "";

    public bool Failed => Status.Equals("Failed", StringComparison.OrdinalIgnoreCase);
}

public sealed record SabnzbdState
{
    public string Version { get; init; } = "";
    public IReadOnlyList<SabJob> Queue { get; init; } = [];
    public IReadOnlyList<SabJob> History { get; init; } = [];

    /// <summary>Category name to its folder (relative to the completed folder, or absolute).</summary>
    public IReadOnlyDictionary<string, string> Categories { get; init; } = new Dictionary<string, string>();

    public string CompleteFolder { get; init; } = "";
    public string IncompleteFolder { get; init; } = "";

    /// <summary>How long SABnzbd keeps its history, in its own words ("keep all", "30 days").</summary>
    public string HistoryRetention { get; init; } = "";
}

public enum ArrApp
{
    Radarr,
    Sonarr,
    Lidarr
}

public sealed record ArrQueueItem
{
    public string DownloadId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Status { get; init; } = "";

    /// <summary>downloading, importPending, importing, imported, failedPending, failed, ignored.</summary>
    public string TrackedState { get; init; } = "";

    public string OutputPath { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string DownloadClient { get; init; } = "";
    public IReadOnlyList<string> Messages { get; init; } = [];
}

public enum ArrEventKind
{
    Grabbed,
    Imported,
    Failed,
    Ignored,
    Other
}

public sealed record ArrHistoryEvent
{
    public string EventType { get; init; } = "";
    public string DownloadId { get; init; } = "";
    public string SourceTitle { get; init; } = "";
    public DateTime Date { get; init; }
    public string DroppedPath { get; init; } = "";
    public string ImportedPath { get; init; } = "";
    public string DownloadClient { get; init; } = "";
    public string Message { get; init; } = "";

    public ArrEventKind Kind => EventType switch
    {
        "grabbed" => ArrEventKind.Grabbed,
        "downloadFolderImported" or "downloadImported" or "trackFileImported" or "movieFolderImported" => ArrEventKind.Imported,
        "downloadFailed" or "importFailed" or "albumImportIncomplete" => ArrEventKind.Failed,
        "downloadIgnored" => ArrEventKind.Ignored,
        _ => ArrEventKind.Other
    };
}

/// <summary>A download client as an arr app has it set up (Settings > Download Clients).</summary>
public sealed record ArrDownloadClient
{
    public string Name { get; init; } = "";

    /// <summary>QBittorrent, Sabnzbd...</summary>
    public string Implementation { get; init; } = "";

    public bool Enabled { get; init; }
    public bool RemoveCompleted { get; init; }
    public bool RemoveFailed { get; init; }
    public string Category { get; init; } = "";
    public string Host { get; init; } = "";
}

public sealed record ArrState
{
    public ArrApp App { get; init; }
    public string Version { get; init; } = "";
    public IReadOnlyList<ArrQueueItem> Queue { get; init; } = [];
    public IReadOnlyList<ArrHistoryEvent> History { get; init; } = [];
    public IReadOnlyList<ArrDownloadClient> Clients { get; init; } = [];
}

/// <summary>The answer of one service: its state, or why it could not be read.</summary>
public sealed record Reading<T>(T? State, string? Error, bool Configured) where T : class
{
    public bool Ok => State != null;

    public static Reading<T> NotConfigured() => new(null, null, false);

    public static Reading<T> Failed(string error) => new(null, error, true);

    public static Reading<T> From(T state) => new(state, null, true);
}
