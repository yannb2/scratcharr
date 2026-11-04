using System.Text.Json;
using Scratcharr.Options;

namespace Scratcharr.Clients;

/// <summary>
/// Reads SABnzbd's API: queue, history, folders and categories. The one change it makes is
/// <see cref="DeleteHistoryAsync"/> (Clean up): history entries only, never their files.
/// </summary>
public sealed class SabnzbdClient(LiveSettings settings, ILogger<SabnzbdClient> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>History entries read per scan (newest first).</summary>
    public const int HistoryLimit = 1000;

    public async Task<Reading<SabnzbdState>> ReadAsync(CancellationToken cancellationToken)
    {
        var options = settings.Sabnzbd;
        if (!options.Configured)
        {
            return Reading<SabnzbdState>.NotConfigured();
        }

        try
        {
            var version = await CallAsync(options, "version", "", cancellationToken);
            var config = await CallAsync(options, "get_config", "", cancellationToken);
            var queue = await CallAsync(options, "queue", "&limit=1000", cancellationToken);
            var history = await CallAsync(options, "history", $"&limit={HistoryLimit}", cancellationToken);
            var state = ParseConfig(config);
            return Reading<SabnzbdState>.From(state with
            {
                Version = ParseVersion(version),
                Queue = ParseQueue(queue, state.IncompleteFolder),
                History = ParseHistory(history)
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or InvalidOperationException)
        {
            logger.LogWarning("SABnzbd: {Message}", e.Message);
            return Reading<SabnzbdState>.Failed(e is TaskCanceledException ? "SABnzbd did not answer in time." : e.Message);
        }
    }

    /// <summary>Removes finished jobs from SABnzbd's history, leaving their files (Recycle moves those).</summary>
    public async Task DeleteHistoryAsync(IReadOnlyCollection<string> nzoIds, CancellationToken cancellationToken)
    {
        if (nzoIds.Count == 0)
        {
            return;
        }

        var options = settings.Sabnzbd;
        if (!options.Configured)
        {
            throw new InvalidOperationException("SABnzbd is not set up.");
        }

        await CallAsync(options, "history", $"&name=delete&del_files=0&value={Uri.EscapeDataString(string.Join(',', nzoIds))}", cancellationToken);
    }

    public static async Task<(bool Ok, string Message)> TestAsync(SabnzbdOptions options, CancellationToken cancellationToken)
    {
        if (!options.Configured)
        {
            return (false, "Fill in the URL and the API key.");
        }

        try
        {
            // The version answers without a key; the queue needs a good one.
            var version = ParseVersion(await CallAsync(options, "version", "", cancellationToken));
            await CallAsync(options, "queue", "&limit=1", cancellationToken);
            return (true, $"Connected to SABnzbd {version}.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or InvalidOperationException)
        {
            return (false, e.Message);
        }
    }

    private static async Task<string> CallAsync(SabnzbdOptions options, string mode, string extra, CancellationToken cancellationToken)
    {
        var url = $"{options.Url.TrimEnd('/')}/api?mode={mode}&output=json&apikey={Uri.EscapeDataString(options.ApiKey)}{extra}";
        using var response = await Http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"SABnzbd answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        // A wrong key still answers 200, with {"status": false, "error": "API Key Incorrect"}.
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            throw new InvalidOperationException($"SABnzbd: {error.GetString()}");
        }

        return json;
    }

    public static string ParseVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Str("version");
    }

    /// <summary>Folders, categories and history retention from get_config.</summary>
    public static SabnzbdState ParseConfig(string json)
    {
        using var document = JsonDocument.Parse(json);
        var config = document.RootElement.Get("config");
        var misc = config.Get("misc");
        var completeFolder = Clean(misc.Str("complete_dir"));
        var categories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var category in config.Get("categories").Items())
        {
            var name = category.Str("name");
            if (name.Length > 0)
            {
                categories[name] = Clean(category.Str("dir"));
            }
        }

        return new SabnzbdState
        {
            CompleteFolder = completeFolder,
            IncompleteFolder = Clean(misc.Str("download_dir")),
            Categories = categories,
            HistoryRetention = Retention(misc)
        };
    }

    /// <summary>SABnzbd 4.3+ has an option and a number; older versions one text ("0" = keep all, "30d").</summary>
    internal static string Retention(JsonElement misc)
    {
        var option = misc.Str("history_retention_option");
        var number = misc.Str("history_retention_number");
        if (option.Length > 0)
        {
            var then = option.EndsWith("-delete", StringComparison.Ordinal) ? "then deleted" : "then archived";
            return option switch
            {
                "all" or "all-archive" => "keeps all",
                "all-delete" => "deletes each job when it finishes",
                _ when option.StartsWith("days", StringComparison.Ordinal) => $"keeps {number} days, {then}",
                _ when option.StartsWith("number", StringComparison.Ordinal) => $"keeps the last {number} jobs, {then}",
                _ => option
            };
        }

        var old = misc.Str("history_retention");
        return old switch
        {
            "" or "0" => "keeps all",
            "-1" => "deletes each job when it finishes",
            _ when old.EndsWith('d') => $"keeps {old[..^1]} days",
            _ => $"keeps the last {old} jobs"
        };
    }

    public static List<SabJob> ParseQueue(string json, string incompleteFolder)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Get("queue").Get("slots").Items().Select(s =>
        {
            var name = s.Str("filename");
            return new SabJob
            {
                NzoId = s.Str("nzo_id"),
                Name = name,
                Category = s.Str("cat"),
                Status = s.Str("status"),
                InQueue = true,
                // A queued job downloads into a folder named after it in the temporary folder.
                Path = incompleteFolder.Length > 0 && name.Length > 0 ? Path.Combine(incompleteFolder, name) : "",
                Bytes = (long)(s.Double("mb") * 1024 * 1024)
            };
        }).ToList();
    }

    public static List<SabJob> ParseHistory(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Get("history").Get("slots").Items().Select(s => new SabJob
        {
            NzoId = s.Str("nzo_id"),
            Name = s.Str("name"),
            Category = s.Str("category"),
            Status = s.Str("status"),
            Path = Clean(s.Str("storage")),
            IncompletePath = Clean(s.Str("path")),
            Bytes = s.Long("bytes"),
            Completed = Json.Unix(s.Long("completed")),
            FailMessage = s.Str("fail_message")
        }).ToList();
    }

    private static string Clean(string path) => path.Length > 1 ? path.TrimEnd('/') : path;
}
