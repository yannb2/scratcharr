using Scratcharr.Scan;

namespace Scratcharr.Options;

/// <summary>
/// Settings from appsettings.json, or from environment variables in Docker:
/// <c>Scratcharr__Qbittorrent__Url=http://gluetun:8080</c>, <c>Scratcharr__Radarr__ApiKey=...</c>.
/// </summary>
public class ScratcharrOptions
{
    public const string Section = "Scratcharr";

    /// <summary>The downloads folder (the scratch pool). Only items under it are listed.</summary>
    public string DownloadsRoot { get; set; } = "/downloads";

    /// <summary>
    /// The folders whose contents are listed, one item per entry. Category folders inside them
    /// (radarr, sonarr, manual...) are opened too, so each torrent or job is its own item.
    /// </summary>
    public List<WatchedFolder> Folders { get; set; } = [];

    /// <summary>Never listed as strays and never touched: other apps' working folders.</summary>
    public List<string> Protected { get; set; } = [];

    /// <summary>
    /// Folder names treated as category folders even when qBittorrent or SABnzbd has no such
    /// category (yet). The categories the clients report are added on every scan.
    /// </summary>
    public List<string> CategoryFolders { get; set; } = [];

    /// <summary>Settings saved on the Settings page and the first time each item was seen.</summary>
    public string DatabasePath { get; set; } = "/config/scratcharr.db";

    /// <summary>Minutes between two scans. The Scan now button starts one at any time.</summary>
    public double ScanMinutes { get; set; } = 15;

    /// <summary>
    /// Off (the default): read-only, like Salvarr. On: Unpack works, and later the copy and clean-up
    /// buttons. The downloads volume must then be mounted read-write.
    /// </summary>
    public bool AllowChanges { get; set; }

    /// <summary>Where unpacked archives go, one folder each (relative to the downloads root). Ripparr finds discs there.</summary>
    public string DiscsFolder { get; set; } = "discs";

    /// <summary>Where "From USB" copies videos for Radarr/Sonarr Manual Import (relative to the downloads root).</summary>
    public string ImportFolder { get; set; } = "import";

    /// <summary>The USB disks and PC shares, one folder each (/mnt/pc/usb01...). Only read: copies never change them.</summary>
    public string UsbRoot { get; set; } = "/mnt/pc";

    /// <summary>Folders in the USB root that are not disks: Ripparr's ISO mounts. Hidden on the From USB page.</summary>
    public List<string> UsbSkip { get; set; } = ["iso-mounts"];

    /// <summary>After a copy from USB, read the copy back and compare its xxHash with the original's.</summary>
    public bool VerifyCopies { get; set; } = true;

    /// <summary>
    /// Where Recycle moves items, one dated folder per day (relative to the downloads root). An item on another
    /// mount inside /downloads goes to a .recycle folder at the top of that mount instead, so the move stays instant.
    /// </summary>
    public string RecycleFolder { get; set; } = ".recycle";

    /// <summary>Days a recycled item is kept before it is deleted for good.</summary>
    public double RecycleDays { get; set; } = 7;

    /// <summary>The weekly summary sent to ntfy.</summary>
    public NtfyOptions Ntfy { get; set; } = new();

    /// <summary>
    /// Ripparr's address as the browser reaches it (http://10.1.72.11:8186), for the "Open in Ripparr" links.
    /// Empty: the links are not shown.
    /// </summary>
    public string RipparrUrl { get; set; } = "";

    public QbittorrentOptions Qbittorrent { get; set; } = new();

    public SabnzbdOptions Sabnzbd { get; set; } = new();

    public ArrOptions Radarr { get; set; } = new();

    public ArrOptions Sonarr { get; set; } = new();

    public ArrOptions Lidarr { get; set; } = new();

    public ToolPaths Tools { get; set; } = new();

    public UpdateCheckOptions Updates { get; set; } = new();

    public static readonly WatchedFolder[] DefaultFolders =
    [
        new() { Name = "Torrents", Path = "torrents", Kind = FolderKind.Torrents },
        new() { Name = "Usenet, complete", Path = "usenet/complete", Kind = FolderKind.Usenet },
        new() { Name = "Usenet, incomplete", Path = "usenet/incomplete", Kind = FolderKind.UsenetIncomplete },
        new() { Name = "Import", Path = "import", Kind = FolderKind.Manual },
        new() { Name = "Discs", Path = "discs", Kind = FolderKind.Manual }
    ];

    public static readonly string[] DefaultProtected = ["ripparr", ".recycle"];

    public static readonly string[] DefaultCategoryFolders = ["radarr", "sonarr", "lidarr", "books", "manual", "prowlarr"];

    /// <summary>
    /// Categories for downloads started by hand: "manual" (added in qBittorrent or SABnzbd) and "prowlarr"
    /// (grabbed from a Prowlarr search). No arr app imports them; they stay until you decide.
    /// </summary>
    public static readonly string[] ManualCategories = ["manual", "prowlarr"];

    public static bool IsManualCategory(string category) =>
        ManualCategories.Contains(category, StringComparer.OrdinalIgnoreCase);

    /// <summary>Fills in the defaults for lists the configuration left empty, and makes folder paths absolute.</summary>
    public ScratcharrOptions WithDefaults()
    {
        if (Folders.Count == 0)
        {
            Folders = DefaultFolders.Select(f => new WatchedFolder { Name = f.Name, Path = f.Path, Kind = f.Kind }).ToList();
        }

        if (Protected.Count == 0)
        {
            Protected = [.. DefaultProtected];
        }

        if (CategoryFolders.Count == 0)
        {
            CategoryFolders = [.. DefaultCategoryFolders];
        }

        // A relative root (dev/downloads when developing) becomes absolute: the scan compares full paths.
        DownloadsRoot = System.IO.Path.GetFullPath(DownloadsRoot);
        DownloadsRoot = DownloadsRoot.Length > 1 ? DownloadsRoot.TrimEnd('/') : DownloadsRoot;
        foreach (var folder in Folders)
        {
            folder.Path = Absolute(folder.Path);
        }

        Protected = Protected.Select(Absolute).ToList();
        DiscsFolder = Absolute(DiscsFolder);
        ImportFolder = Absolute(ImportFolder);
        RecycleFolder = Absolute(RecycleFolder);
        if (RecycleDays < 1)
        {
            RecycleDays = 1;
        }

        UsbRoot = Paths.Clean(System.IO.Path.GetFullPath(UsbRoot));
        if (ScanMinutes < 1)
        {
            ScanMinutes = 1;
        }

        return this;
    }

    /// <summary>A path relative to the downloads root becomes absolute; an absolute one stays.</summary>
    public string Absolute(string path)
    {
        var full = path.StartsWith('/') ? path : System.IO.Path.Combine(DownloadsRoot, path);
        return full.Length > 1 ? full.TrimEnd('/') : full;
    }
}

public enum FolderKind
{
    /// <summary>qBittorrent saves here (directly or in category folders).</summary>
    Torrents,

    /// <summary>SABnzbd's completed folder.</summary>
    Usenet,

    /// <summary>SABnzbd's temporary folder: everything in it belongs to a running job or is left over.</summary>
    UsenetIncomplete,

    /// <summary>Filled by hand (USB copies, files to import, discs for Ripparr).</summary>
    Manual
}

public class WatchedFolder
{
    public string Name { get; set; } = "";

    /// <summary>Relative to the downloads root, or absolute.</summary>
    public string Path { get; set; } = "";

    public FolderKind Kind { get; set; }
}

public class QbittorrentOptions
{
    /// <summary>qBittorrent sits behind gluetun: http://gluetun:8080 on the compose network.</summary>
    public string Url { get; set; } = "";

    /// <summary>qBittorrent 5.2+ (Options > WebUI > API key). Wins over the username and password.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Empty when an API key is set, or when qBittorrent lets this subnet in without a login.</summary>
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Configured => !string.IsNullOrWhiteSpace(Url);
}

public class SabnzbdOptions
{
    /// <summary>For example http://sabnzbd:8080.</summary>
    public string Url { get; set; } = "";

    public string ApiKey { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Configured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}

public class ArrOptions
{
    /// <summary>For example http://radarr:7878 on the compose network.</summary>
    public string Url { get; set; } = "";

    public string ApiKey { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Configured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>Command names, or full paths when a tool is not on the PATH.</summary>
public class ToolPaths
{
    public string SevenZip { get; set; } = "7zz";
}

public class UpdateCheckOptions
{
    /// <summary>Look for new tool versions on the internet. Nothing is ever installed by the app.</summary>
    public bool Enabled { get; set; } = true;

    public double IntervalHours { get; set; } = 24;
}

public class NtfyOptions
{
    /// <summary>The topic URL (https://ntfy.sh/my-topic or http://ntfy/scratcharr). Empty: no summary. NTFY_URL in compose.</summary>
    public string Url { get; set; } = "";

    /// <summary>An access token when the topic needs one.</summary>
    public string Token { get; set; } = "";

    /// <summary>The day and local hour the weekly summary goes out.</summary>
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;

    public int Hour { get; set; } = 9;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Configured => !string.IsNullOrWhiteSpace(Url);
}
