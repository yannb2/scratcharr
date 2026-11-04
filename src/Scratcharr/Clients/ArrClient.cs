using System.Text.Json;
using Scratcharr.Options;

namespace Scratcharr.Clients;

/// <summary>
/// Reads Radarr, Sonarr or Lidarr: the queue, the recent history and the download clients
/// (category, Remove Completed, Remove Failed). Read-only.
/// </summary>
public sealed class ArrClient(LiveSettings settings, ILogger<ArrClient> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>History events read per scan (newest first): about a month for a busy library.</summary>
    public const int HistoryLimit = 2000;

    public ArrOptions Options(ArrApp app) => app switch
    {
        ArrApp.Radarr => settings.Radarr,
        ArrApp.Sonarr => settings.Sonarr,
        _ => settings.Lidarr
    };

    /// <summary>Lidarr still has its v1 API; Radarr and Sonarr are on v3.</summary>
    public static string ApiBase(ArrApp app) => app == ArrApp.Lidarr ? "api/v1" : "api/v3";

    public async Task<Reading<ArrState>> ReadAsync(ArrApp app, CancellationToken cancellationToken)
    {
        var options = Options(app);
        if (!options.Configured)
        {
            return Reading<ArrState>.NotConfigured();
        }

        try
        {
            var api = ApiBase(app);
            var status = await GetAsync(app, options, $"{api}/system/status", cancellationToken);
            var queue = await GetAsync(app, options,
                $"{api}/queue?page=1&pageSize=1000&includeUnknownMovieItems=true&includeUnknownSeriesItems=true&includeUnknownArtistItems=true",
                cancellationToken);
            var history = await GetAsync(app, options,
                $"{api}/history?page=1&pageSize={HistoryLimit}&sortKey=date&sortDirection=descending", cancellationToken);
            var clients = await GetAsync(app, options, $"{api}/downloadclient", cancellationToken);
            return Reading<ArrState>.From(new ArrState
            {
                App = app,
                Version = ParseVersion(status),
                Queue = ParseQueue(queue),
                History = ParseHistory(history),
                Clients = ParseClients(clients)
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or InvalidOperationException)
        {
            logger.LogWarning("{App}: {Message}", app, e.Message);
            return Reading<ArrState>.Failed(e is TaskCanceledException ? $"{app} did not answer in time." : e.Message);
        }
    }

    public static async Task<(bool Ok, string Message)> TestAsync(ArrApp app, ArrOptions options, CancellationToken cancellationToken)
    {
        if (!options.Configured)
        {
            return (false, "Fill in the URL and the API key.");
        }

        try
        {
            var status = await GetAsync(app, options, $"{ApiBase(app)}/system/status", cancellationToken);
            return (true, $"Connected to {app} {ParseVersion(status)}".TrimEnd() + ".");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or InvalidOperationException)
        {
            return (false, e is TaskCanceledException ? $"{app} did not answer in time." : e.Message);
        }
    }

    private static async Task<string> GetAsync(ArrApp app, ArrOptions options, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.Url.TrimEnd('/')}/{path}");
        request.Headers.Add("X-Api-Key", options.ApiKey);
        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? $"{app} refused the API key."
                : $"{app} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public static string ParseVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Str("version");
    }

    /// <summary>The queue comes paged ({"records": [...]}); older versions answered a plain list.</summary>
    private static IEnumerable<JsonElement> Records(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : root.Get("records").Items();

    public static List<ArrQueueItem> ParseQueue(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Records(document.RootElement).Select(r => new ArrQueueItem
        {
            DownloadId = r.Str("downloadId").ToUpperInvariant(),
            Title = r.Str("title"),
            Status = r.Str("status"),
            TrackedState = r.Str("trackedDownloadState"),
            OutputPath = Clean(r.Str("outputPath")),
            Protocol = r.Str("protocol"),
            DownloadClient = r.Str("downloadClient"),
            Messages = r.Get("statusMessages").Items()
                .SelectMany(m => m.Get("messages").Items().Select(x => x.GetString() ?? "").Prepend(m.Str("title")))
                .Where(m => m.Length > 0)
                .Distinct()
                .ToList()
        }).ToList();
    }

    public static List<ArrHistoryEvent> ParseHistory(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Records(document.RootElement).Select(r =>
        {
            var data = r.Get("data");
            var client = data.Str("downloadClientName");
            return new ArrHistoryEvent
            {
                EventType = r.Str("eventType"),
                DownloadId = r.Str("downloadId").ToUpperInvariant(),
                SourceTitle = r.Str("sourceTitle"),
                Date = r.Date("date") ?? DateTime.MinValue,
                DroppedPath = Clean(data.Str("droppedPath")),
                ImportedPath = Clean(data.Str("importedPath")),
                DownloadClient = client.Length > 0 ? client : data.Str("downloadClient"),
                Message = data.Str("message") is { Length: > 0 } message ? message : data.Str("reason")
            };
        }).ToList();
    }

    public static List<ArrDownloadClient> ParseClients(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Items().Select(c =>
        {
            var fields = c.Get("fields").Items()
                .GroupBy(f => f.Str("name"))
                .ToDictionary(g => g.Key, g => g.First().Str("value"));
            var category = fields.GetValueOrDefault("movieCategory") ?? fields.GetValueOrDefault("tvCategory")
                ?? fields.GetValueOrDefault("musicCategory") ?? fields.GetValueOrDefault("category") ?? "";
            var host = fields.GetValueOrDefault("host") ?? "";
            var port = fields.GetValueOrDefault("port") ?? "";
            return new ArrDownloadClient
            {
                Name = c.Str("name"),
                Implementation = c.Str("implementation"),
                Enabled = c.Bool("enable"),
                RemoveCompleted = c.Bool("removeCompletedDownloads"),
                RemoveFailed = c.Bool("removeFailedDownloads"),
                Category = category,
                Host = host.Length > 0 && port.Length > 0 ? $"{host}:{port}" : host
            };
        }).ToList();
    }

    private static string Clean(string path) => path.Length > 1 ? path.TrimEnd('/') : path;
}
