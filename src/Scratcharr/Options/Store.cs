using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Scratcharr.Options;

/// <summary>
/// The small SQLite database in /config: settings saved on the Settings page, and when each
/// item was first seen (an item with no file times of its own still gets an age).
/// </summary>
public sealed class Store
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _connectionString;

    public Store(IOptions<ScratcharrOptions> options) : this(options.Value.DatabasePath)
    {
    }

    public Store(string databasePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS seen (path TEXT PRIMARY KEY, first TEXT NOT NULL, last TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public string? Setting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    /// <summary>Saves a setting; null removes it.</summary>
    public void SetSetting(string key, string? value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (value == null)
        {
            command.CommandText = "DELETE FROM settings WHERE key = $key";
        }
        else
        {
            command.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = $value";
            command.Parameters.AddWithValue("$value", value);
        }

        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Records that these paths exist now and returns when each was first seen. Paths not seen
    /// for 30 days are forgotten, so the table stays small.
    /// </summary>
    public Dictionary<string, DateTime> MarkSeen(IReadOnlyCollection<string> paths, DateTime nowUtc)
    {
        var now = nowUtc.ToString("O");
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO seen (path, first, last) VALUES ($path, $now, $now)
                ON CONFLICT(path) DO UPDATE SET last = $now
                RETURNING first
                """;
            var path = upsert.Parameters.Add("$path", SqliteType.Text);
            upsert.Parameters.AddWithValue("$now", now);
            foreach (var p in paths)
            {
                path.Value = p;
                if (upsert.ExecuteScalar() is string first)
                {
                    result[p] = DateTime.Parse(first, null, System.Globalization.DateTimeStyles.RoundtripKind);
                }
            }
        }

        using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM seen WHERE last < $before";
            cleanup.Parameters.AddWithValue("$before", nowUtc.AddDays(-30).ToString("O"));
            cleanup.ExecuteNonQuery();
        }

        transaction.Commit();
        return result;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}

/// <summary>
/// The connections changed on the Settings page. They are kept in the database and win over the
/// environment variables (Scratcharr__Radarr__Url...), which stay as the starting values until the
/// page saves something.
/// </summary>
public sealed class LiveSettings(IOptions<ScratcharrOptions> options, Store store)
{
    public const string QbittorrentKey = "integrations.qbittorrent";
    public const string SabnzbdKey = "integrations.sabnzbd";
    public const string RadarrKey = "integrations.radarr";
    public const string SonarrKey = "integrations.sonarr";
    public const string LidarrKey = "integrations.lidarr";

    public QbittorrentOptions Qbittorrent => Read(QbittorrentKey, options.Value.Qbittorrent);

    public SabnzbdOptions Sabnzbd => Read(SabnzbdKey, options.Value.Sabnzbd);

    public ArrOptions Radarr => Read(RadarrKey, options.Value.Radarr);

    public ArrOptions Sonarr => Read(SonarrKey, options.Value.Sonarr);

    public ArrOptions Lidarr => Read(LidarrKey, options.Value.Lidarr);

    public event Action? Changed;

    /// <summary>True when the page saved this setting; false when it still comes from the environment.</summary>
    public bool Saved(string key) => store.Setting(key) != null;

    public void Save<T>(string key, T value)
    {
        store.SetSetting(key, JsonSerializer.Serialize(value, Store.Json));
        Changed?.Invoke();
    }

    /// <summary>Forgets what the page saved: the environment variables apply again.</summary>
    public void Reset(string key)
    {
        store.SetSetting(key, null);
        Changed?.Invoke();
    }

    private T Read<T>(string key, T fallback) where T : class
    {
        var json = store.Setting(key);
        if (json == null)
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Store.Json) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
