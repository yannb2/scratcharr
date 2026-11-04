using Microsoft.Data.Sqlite;

namespace Scratcharr.Options;

/// <summary>An item Recycle moved to the recycle folder: where it was, where it is, when.</summary>
public sealed record RecycledItem
{
    public long Id { get; init; }
    public required string Original { get; init; }
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime Recycled { get; init; }
    public string Verdict { get; init; } = "";
    public string Note { get; init; } = "";

    public string Name => System.IO.Path.GetFileName(Original);

    public DateTime DeleteAt(double days) => Recycled.AddDays(days);
}

/// <summary>One line of the action log: every change Scratcharr made, or tried to.</summary>
public sealed record ActionEntry(long Id, DateTime Time, string Action, string Path, long Size, string Detail, bool Ok);

public sealed partial class Store
{
    public long AddRecycled(RecycledItem item)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recycled (original, path, size, recycled, verdict, note)
            VALUES ($original, $path, $size, $recycled, $verdict, $note) RETURNING id
            """;
        command.Parameters.AddWithValue("$original", item.Original);
        command.Parameters.AddWithValue("$path", item.Path);
        command.Parameters.AddWithValue("$size", item.Size);
        command.Parameters.AddWithValue("$recycled", item.Recycled.ToString("O"));
        command.Parameters.AddWithValue("$verdict", item.Verdict);
        command.Parameters.AddWithValue("$note", item.Note);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Everything in the recycle folder, oldest first.</summary>
    public List<RecycledItem> Recycled()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, original, path, size, recycled, verdict, note FROM recycled ORDER BY recycled, id";
        using var reader = command.ExecuteReader();
        var items = new List<RecycledItem>();
        while (reader.Read())
        {
            items.Add(new RecycledItem
            {
                Id = reader.GetInt64(0),
                Original = reader.GetString(1),
                Path = reader.GetString(2),
                Size = reader.GetInt64(3),
                Recycled = Time(reader.GetString(4)),
                Verdict = reader.GetString(5),
                Note = reader.GetString(6)
            });
        }

        return items;
    }

    public void RemoveRecycled(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recycled WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void AddAction(string action, string path, long size, string detail, bool ok = true)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO actions (time, action, path, size, detail, ok) VALUES ($time, $action, $path, $size, $detail, $ok);
            DELETE FROM actions WHERE id <= (SELECT MAX(id) FROM actions) - 2000;
            """;
        command.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$ok", ok ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>The newest actions first.</summary>
    public List<ActionEntry> Actions(int limit = 100, DateTime? since = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, time, action, path, size, detail, ok FROM actions WHERE time >= $since ORDER BY id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$since", (since ?? DateTime.MinValue).ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var entries = new List<ActionEntry>();
        while (reader.Read())
        {
            entries.Add(new ActionEntry(reader.GetInt64(0), Time(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6) == 1));
        }

        return entries;
    }

    private static DateTime Time(string text) => DateTime.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
