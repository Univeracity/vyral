using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vyral.Providers.Abstractions;

namespace Vyral.Server;

public interface IProviderRunJobPersistence
{
    string Kind { get; }
    IReadOnlyList<ProviderRunJob> LoadLatest(int limit);
    void Upsert(ProviderRunJob job);
    void Delete(string id);
    void PruneTerminal(int maxRetainedTerminalJobs);
}

public sealed class SqliteProviderRunJobPersistence : IProviderRunJobPersistence
{
    private readonly string _connectionString;

    public SqliteProviderRunJobPersistence(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public string Kind => "sqlite";

    public IReadOnlyList<ProviderRunJob> LoadLatest(int limit)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT job_json
            FROM vyral_provider_run_jobs
            ORDER BY created_at DESC, id ASC
            LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        var jobs = new List<ProviderRunJob>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var job = JsonSerializer.Deserialize<ProviderRunJob>(reader.GetString(0), ProviderJson.Options);
            if (job is not null)
            {
                jobs.Add(job);
            }
        }

        return jobs;
    }

    public void Upsert(ProviderRunJob job)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO vyral_provider_run_jobs (
                id,
                provider,
                status,
                created_at,
                completed_at,
                job_json
            )
            VALUES (
                $id,
                $provider,
                $status,
                $created_at,
                $completed_at,
                $job_json
            )
            ON CONFLICT(id) DO UPDATE SET
                provider = excluded.provider,
                status = excluded.status,
                created_at = excluded.created_at,
                completed_at = excluded.completed_at,
                job_json = excluded.job_json;";
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$provider", job.Provider);
        command.Parameters.AddWithValue("$status", job.Status.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$created_at", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$completed_at", job.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$job_json", JsonSerializer.Serialize(job, ProviderJson.Options));
        command.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM vyral_provider_run_jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void PruneTerminal(int maxRetainedTerminalJobs)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM vyral_provider_run_jobs
            WHERE status NOT IN ('queued', 'running')
                AND id NOT IN (
                    SELECT id
                    FROM vyral_provider_run_jobs
                    WHERE status NOT IN ('queued', 'running')
                    ORDER BY created_at DESC, id ASC
                    LIMIT $max_retained
                );";
        command.Parameters.AddWithValue("$max_retained", maxRetainedTerminalJobs);
        command.ExecuteNonQuery();
    }
}
