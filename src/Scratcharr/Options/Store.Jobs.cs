using System.Globalization;
using Microsoft.Data.Sqlite;
using Scratcharr.Jobs;

namespace Scratcharr.Options;

/// <summary>The jobs table: unpack jobs, queued, running and finished, with their logs.</summary>
public sealed partial class Store
{
    public long AddJob(Job job)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (kind, source, target, item, state, created, size)
            VALUES ($kind, $source, $target, $item, $state, $created, $size) RETURNING id
            """;
        command.Parameters.AddWithValue("$kind", job.Kind.ToString());
        command.Parameters.AddWithValue("$source", job.Source);
        command.Parameters.AddWithValue("$target", job.Target);
        command.Parameters.AddWithValue("$item", job.Item);
        command.Parameters.AddWithValue("$state", job.State.ToString());
        command.Parameters.AddWithValue("$created", job.Created.ToString("O"));
        command.Parameters.AddWithValue("$size", job.Size);
        return (long)command.ExecuteScalar()!;
    }

    public void UpdateJob(Job job)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs SET state = $state, started = $started, finished = $finished, message = $message, log = $log, size = $size
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$state", job.State.ToString());
        command.Parameters.AddWithValue("$started", (object?)job.Started?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$finished", (object?)job.Finished?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$message", job.Message);
        command.Parameters.AddWithValue("$log", job.Log);
        command.Parameters.AddWithValue("$size", job.Size);
        command.ExecuteNonQuery();
    }

    public Job? Job(long id) => QueryJobs("WHERE id = $id", ("$id", id)).FirstOrDefault();

    /// <summary>Open jobs first (oldest first), then the most recent finished ones.</summary>
    public IReadOnlyList<Job> Jobs(int finished = 50) =>
    [
        .. QueryJobs("WHERE state IN ('Queued', 'Running') ORDER BY id"),
        .. QueryJobs("WHERE state NOT IN ('Queued', 'Running') ORDER BY id DESC LIMIT $limit", ("$limit", finished))
    ];

    private List<Job> QueryJobs(string where, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, kind, source, target, item, state, created, started, finished, size, message, log FROM jobs {where}";
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        var jobs = new List<Job>();
        while (reader.Read())
        {
            jobs.Add(new Job
            {
                Id = reader.GetInt64(0),
                Kind = Enum.TryParse<JobKind>(reader.GetString(1), out var kind) ? kind : JobKind.Unpack,
                Source = reader.GetString(2),
                Target = reader.GetString(3),
                Item = reader.GetString(4),
                State = Enum.TryParse<JobState>(reader.GetString(5), out var state) ? state : JobState.Failed,
                Created = Date(reader, 6) ?? DateTime.MinValue,
                Started = Date(reader, 7),
                Finished = Date(reader, 8),
                Size = reader.GetInt64(9),
                Message = reader.GetString(10),
                Log = reader.GetString(11)
            });
        }

        return jobs;
    }

    private static DateTime? Date(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : DateTime.Parse(reader.GetString(column), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
