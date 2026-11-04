using System.Net;
using System.Text.Json;
using Scratcharr.Options;

namespace Scratcharr.Clients;

/// <summary>
/// Reads qBittorrent's Web API v2: the torrents, the categories and the seeding limits. The one change it makes is
/// <see cref="RemoveAsync"/> (Clean up), which takes torrents out of qBittorrent and leaves their files for Recycle.
/// </summary>
public sealed class QbittorrentClient(LiveSettings settings, ILogger<QbittorrentClient> logger)
{
    private readonly CookieContainer _cookies = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private HttpClient? _http;
    private string? _forUrl;

    public async Task<Reading<QbittorrentState>> ReadAsync(CancellationToken cancellationToken)
    {
        var options = settings.Qbittorrent;
        if (!options.Configured)
        {
            return Reading<QbittorrentState>.NotConfigured();
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var http = Client(options);
            var version = await GetAsync(http, options, "api/v2/app/version", cancellationToken);
            var torrents = await GetAsync(http, options, "api/v2/torrents/info", cancellationToken);
            var categories = await GetAsync(http, options, "api/v2/torrents/categories", cancellationToken);
            var preferences = await GetAsync(http, options, "api/v2/app/preferences", cancellationToken);
            var (limits, savePath) = ParsePreferences(preferences);
            return Reading<QbittorrentState>.From(new QbittorrentState
            {
                Version = version.Trim(),
                Torrents = ParseTorrents(torrents),
                Categories = ParseCategories(categories),
                Limits = limits,
                DefaultSavePath = savePath
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or InvalidOperationException)
        {
            logger.LogWarning("qBittorrent: {Message}", e.Message);
            return Reading<QbittorrentState>.Failed(Describe(e));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes torrents from qBittorrent without deleting their files: Recycle moves the files afterwards, so they
    /// can still be restored for 7 days and qBittorrent never sees files vanish under a torrent it still lists.
    /// </summary>
    public async Task RemoveAsync(IReadOnlyCollection<string> hashes, CancellationToken cancellationToken)
    {
        if (hashes.Count == 0)
        {
            return;
        }

        var options = settings.Qbittorrent;
        if (!options.Configured)
        {
            throw new InvalidOperationException("qBittorrent is not set up.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var http = Client(options);
            var form = new Dictionary<string, string>
            {
                ["hashes"] = string.Join('|', hashes.Select(h => h.ToLowerInvariant())),
                ["deleteFiles"] = "false"
            };
            using var response = await http.PostAsync("api/v2/torrents/delete", new FormUrlEncodedContent(form), cancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                await LoginAsync(http, options, cancellationToken);
                using var again = await http.PostAsync("api/v2/torrents/delete", new FormUrlEncodedContent(form), cancellationToken);
                await Body(again, cancellationToken);
                return;
            }

            await Body(response, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The Test button: logs in and asks the version.</summary>
    public static async Task<(bool Ok, string Message)> TestAsync(QbittorrentOptions options, CancellationToken cancellationToken)
    {
        if (!options.Configured)
        {
            return (false, "Fill in the URL.");
        }

        try
        {
            using var http = NewClient(options, new CookieContainer());
            var version = await GetAsync(http, options, "api/v2/app/version", cancellationToken);
            return (true, $"Connected to qBittorrent {version.Trim()}.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return (false, Describe(e));
        }
    }

    private HttpClient Client(QbittorrentOptions options)
    {
        var key = $"{options.Url}|{options.ApiKey}|{options.Username}|{options.Password}";
        if (_http == null || _forUrl != key)
        {
            _http?.Dispose();
            _http = NewClient(options, _cookies);
            _forUrl = key;
        }

        return _http;
    }

    internal static HttpClient NewClient(QbittorrentOptions options, CookieContainer cookies)
    {
        var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        var http = new HttpClient(handler) { BaseAddress = new Uri(options.Url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) };
        // qBittorrent refuses requests whose Referer/Origin don't match its host when they are sent; send its own.
        http.DefaultRequestHeaders.Referrer = http.BaseAddress;
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            // qBittorrent 5.2+: a stateless API key replaces the cookie login.
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());
        }

        return http;
    }

    /// <summary>GET, logging in first when qBittorrent answers 403 (no session yet, or it expired).</summary>
    private static async Task<string> GetAsync(HttpClient http, QbittorrentOptions options, string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            await LoginAsync(http, options, cancellationToken);
            using var again = await http.GetAsync(path, cancellationToken);
            return await Body(again, cancellationToken);
        }

        return await Body(response, cancellationToken);
    }

    private static async Task LoginAsync(HttpClient http, QbittorrentOptions options, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "qBittorrent refused the API key: check it in Tools > Options > WebUI > API key (qBittorrent 5.2 or later).");
        }

        if (string.IsNullOrEmpty(options.Username))
        {
            throw new InvalidOperationException(
                "qBittorrent asks for a login: fill in its API key (5.2 or later) or the username and password, or allow this subnet without one (Web UI > Authentication).");
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = options.Username,
            ["password"] = options.Password
        });
        using var response = await http.PostAsync("api/v2/auth/login", form, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || !text.StartsWith("Ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(response.StatusCode == HttpStatusCode.Forbidden
                ? "qBittorrent blocked this address after too many failed logins; wait and try again."
                : "qBittorrent refused the username or password.");
        }
    }

    private static async Task<string> Body(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"qBittorrent answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string Describe(Exception e) => e is TaskCanceledException ? "qBittorrent did not answer in time." : e.Message;

    public static List<Torrent> ParseTorrents(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Items().Select(t => new Torrent
        {
            Hash = t.Str("hash").ToUpperInvariant(),
            Name = t.Str("name"),
            Category = t.Str("category"),
            State = t.Str("state"),
            ContentPath = Clean(t.Str("content_path")),
            SavePath = Clean(t.Str("save_path")),
            Size = t.Long("total_size", t.Long("size")),
            Progress = t.Double("progress"),
            SeedingSeconds = t.Long("seeding_time"),
            SeedingLimitMinutes = t.Long("seeding_time_limit", -2),
            Ratio = t.Double("ratio"),
            RatioLimit = t.Double("ratio_limit", -2),
            Added = Json.Unix(t.Long("added_on")),
            Completed = Json.Unix(t.Long("completion_on"))
        }).ToList();
    }

    public static Dictionary<string, string> ParseCategories(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var category in document.RootElement.EnumerateObject())
            {
                result[category.Name] = Clean(category.Value.Str("savePath"));
            }
        }

        return result;
    }

    public static (SeedingLimits Limits, string SavePath) ParsePreferences(string json)
    {
        using var document = JsonDocument.Parse(json);
        var p = document.RootElement;
        var action = p.Long("max_ratio_act");
        return (new SeedingLimits
        {
            Minutes = p.Bool("max_seeding_time_enabled") ? p.Long("max_seeding_time") : null,
            Ratio = p.Bool("max_ratio_enabled") ? p.Double("max_ratio") : null,
            Action = Enum.IsDefined(typeof(ShareLimitAction), (int)action) ? (ShareLimitAction)action : ShareLimitAction.Stop
        }, Clean(p.Str("save_path")));
    }

    /// <summary>qBittorrent paths sometimes end in a slash; the scan's don't.</summary>
    internal static string Clean(string path) => path.Length > 1 ? path.TrimEnd('/') : path;
}
