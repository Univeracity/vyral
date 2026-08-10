using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Pgvector;

public class PgvectorRecordCollectionStore : IRecordCollectionStore
{
    private readonly string? _connectionString;
    private readonly NpgsqlDataSource? _dataSource;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PgvectorRecordCollectionStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected PgvectorRecordCollectionStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        var migrationManager = _dataSource != null
            ? new PgvectorMigrationManager(_dataSource)
            : new PgvectorMigrationManager(_connectionString!);
        return migrationManager.MigrateAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Collections
    // -------------------------------------------------------------------------

    public async Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default)
    {
        ValidateCollectionPolicy(policy);

        var existing = await GetCollectionPolicyAsync(policy.Name, ct);
        if (existing != null)
        {
            if (!RecordCollectionPolicyComparer.AreEquivalent(existing, policy))
                throw new InvalidOperationException($"Collection '{policy.Name}' already exists with a different policy.");
            return;
        }

        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO vyral_collections (name, policy_json) VALUES ($1, $2::jsonb)";
            cmd.Parameters.AddWithValue(policy.Name);
            cmd.Parameters.AddWithValue(JsonSerializer.Serialize(policy, JsonOptions));
            await cmd.ExecuteNonQueryAsync(ct);

            // Create pgvector indexes for each vector field
            foreach (var vp in policy.VectorPolicies)
            {
                var idxDdl = PgvectorVectorPolicyMapper.BuildCreateIndexSql(policy.Name, vp.Name, vp);
                if (!string.IsNullOrWhiteSpace(idxDdl))
                {
                    await using var idxCmd = conn.CreateCommand();
                    idxCmd.Transaction = tx;
                    idxCmd.CommandText = idxDdl;
                    await idxCmd.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM vyral_collections ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        return names;
    }

    public async Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(string collection, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT policy_json FROM vyral_collections WHERE name = $1";
        cmd.Parameters.AddWithValue(collection);
        var raw = await cmd.ExecuteScalarAsync(ct);
        if (raw == null || raw == DBNull.Value) return null;
        return JsonSerializer.Deserialize<RecordCollectionPolicy>(raw.ToString()!, JsonOptions);
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            // Children are deleted via ON DELETE CASCADE from vyral_records and vyral_record_vectors
            await ExecAsync(conn, tx, "DELETE FROM vyral_record_metadata_index WHERE collection = $1", ct, collection);
            await ExecAsync(conn, tx, "DELETE FROM vyral_record_vectors WHERE collection = $1", ct, collection);
            await ExecAsync(conn, tx, "DELETE FROM vyral_records WHERE collection = $1", ct, collection);
            await ExecAsync(conn, tx, "DELETE FROM vyral_collections WHERE name = $1", ct, collection);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Records
    // -------------------------------------------------------------------------

    public async Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default)
    {
        await UpsertRecordAsync(collection, record, precondition: null, ct);
    }

    public async Task UpsertRecordAsync(string collection, VyralRecord record, RecordWritePrecondition? precondition, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidateRecord(record);

        var policy = await GetCollectionPolicyAsync(collection, ct)
            ?? throw new InvalidOperationException($"Collection '{collection}' does not exist.");
        RecordVectorValidator.ValidateRecordVectors(collection, policy, record);

        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await UpsertRecordCoreAsync(conn, tx, collection, record, policy, precondition, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public Task<RecordBatchUpsertResult> UpsertRecordsAsync(
        string collection,
        RecordBatchUpsertRequest request,
        CancellationToken ct = default)
        => UpsertRecordsBatchAsync(collection, request, ct);

    private async Task<RecordBatchUpsertResult> UpsertRecordsBatchAsync(
        string collection,
        RecordBatchUpsertRequest request,
        CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        request.ValidatePreconditionAlignment();
        var records = request.Records;

        var policy = await GetCollectionPolicyAsync(collection, ct)
            ?? throw new InvalidOperationException($"Collection '{collection}' does not exist.");

        var result = new RecordBatchUpsertResult
        {
            Collection = collection,
            Requested = records.Count
        };

        await using var conn = await OpenAsync(ct);
        for (var i = 0; i < records.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var record = records[i];
            var item = new RecordBatchUpsertItemResult
            {
                Index = i,
                Id = record.Id,
                PartitionKey = record.PartitionKey
            };

            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                RecordIdentityValidator.ValidateRecord(record);
                RecordVectorValidator.ValidateRecordVectors(collection, policy, record);
                await UpsertRecordCoreAsync(conn, tx, collection, record, policy, request.GetPrecondition(i), ct);
                await tx.CommitAsync(ct);
                item.Status = RecordUpsertStatuses.Succeeded;
                item.Etag = record.Etag;
                item.Revision = record.Revision;
                result.Succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await tx.RollbackAsync(ct);
                item.Status = RecordUpsertStatuses.Failed;
                item.Error = ex.Message;
                result.Failed++;
            }

            result.Attempted++;
            result.Items.Add(item);

            if (item.Status == RecordUpsertStatuses.Failed && !request.ContinueOnError)
            {
                result.StoppedOnError = i + 1 < records.Count;
                break;
            }
        }

        return result;
    }

    public async Task<VyralRecord?> GetRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidatePartitionKey(partitionKey);
        RecordIdentityValidator.ValidateRecordId(id);

        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content_json FROM vyral_records WHERE collection = $1 AND partition_key = $2 AND id = $3";
        cmd.Parameters.AddWithValue(collection);
        cmd.Parameters.AddWithValue(partitionKey);
        cmd.Parameters.AddWithValue(id);
        var raw = await cmd.ExecuteScalarAsync(ct);
        if (raw == null || raw == DBNull.Value) return null;
        return JsonSerializer.Deserialize<VyralRecord>(raw.ToString()!, JsonOptions);
    }

    public async Task DeleteRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidatePartitionKey(partitionKey);
        RecordIdentityValidator.ValidateRecordId(id);

        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            // Cascade deletes vectors and metadata index via FK
            await ExecAsync(conn, tx,
                "DELETE FROM vyral_records WHERE collection = $1 AND partition_key = $2 AND id = $3",
                ct, collection, partitionKey, id);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Query
    // -------------------------------------------------------------------------

    public async Task<RecordQueryResult> QueryRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        if (query.Limit.HasValue && query.Limit.Value <= 0)
            throw new InvalidOperationException("Query page size must be greater than zero.");
        var policy = await GetCollectionPolicyAsync(collection, ct)
            ?? throw new InvalidOperationException($"Collection '{collection}' does not exist.");

        var qb = new PgvectorQueryBuilder();
        var (sql, parameters) = qb.BuildQuery(collection, query, policy.IndexedMetadata);
        var offset = PgvectorQueryBuilder.DecodeContinuationToken(query.ContinuationToken);
        var pageSize = query.Limit;

        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var param in parameters)
        {
            // Re-key parameters to positional style Npgsql expects
            cmd.Parameters.Add(param);
        }

        var items = new List<VyralRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(JsonSerializer.Deserialize<VyralRecord>(reader.GetString(0), JsonOptions)!);

        string? next = null;
        if (pageSize.HasValue && items.Count == pageSize.Value)
        {
            next = PgvectorQueryBuilder.EncodeContinuationToken(offset + items.Count);
        }

        return new RecordQueryResult { Items = items, ContinuationToken = next };
    }

    // -------------------------------------------------------------------------
    // Search
    // -------------------------------------------------------------------------

    public async Task<RecordSearchResult> SearchRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        if (query.Limit.HasValue && query.Limit.Value <= 0)
            throw new InvalidOperationException("Search page size must be greater than zero.");

        if (query.Vector == null)
        {
            if (query.Lexical != null) return await SearchLexicalPageAsync(collection, query, ct);
            var records = await QueryRecordsPageAsync(collection, query, ct);
            return new RecordSearchResult
            {
                Items = records.Items.Select(r => new VyralRecordMatch { Record = r, Score = 1.0f }).ToList(),
                ContinuationToken = records.ContinuationToken
            };
        }

        if (query.Lexical != null)
            throw new InvalidOperationException("Combined vector and lexical search is provided by the retrieval service; collection search accepts one search mode at a time.");

        var policy = await GetCollectionPolicyAsync(collection, ct)
            ?? throw new InvalidOperationException($"Collection '{collection}' does not exist.");
        var fieldPolicy = RecordVectorValidator.ValidateSearchVector(collection, policy, query.Vector);

        var qb = new PgvectorQueryBuilder();
        var (sql, parameters) = qb.BuildVectorSearchQuery(collection, query, fieldPolicy, policy.IndexedMetadata);

        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var param in parameters) cmd.Parameters.Add(param);

        var results = new List<VyralRecordMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var record = JsonSerializer.Deserialize<VyralRecord>(reader.GetString(0), JsonOptions)!;
            var distance = reader.GetFloat(1);
            var score = PgvectorVectorPolicyMapper.DistanceToScore(fieldPolicy.DistanceFunction, distance);

            if (query.Vector.MinScore == null || score >= query.Vector.MinScore)
            {
                results.Add(new VyralRecordMatch
                {
                    Record = record,
                    Score = score,
                    Diagnostics = new RetrievalDiagnostics
                    {
                        ResultIdentity = new RetrievalResultIdentity
                        {
                            Collection = collection,
                            PartitionKey = record.PartitionKey,
                            Id = record.Id,
                            Type = record.Type,
                            Etag = record.Etag,
                            Revision = record.Revision
                        },
                        CandidateSources = new List<string> { "vector" },
                        ReasonCodes = new List<string>
                        {
                            "result.identity.record",
                            "mode.vector",
                            "candidate.source.vector",
                            "score.vector.raw_similarity"
                        },
                        ScoreComponents = new Dictionary<string, float> { ["vector"] = score },
                        Details = new Dictionary<string, object?>
                        {
                            ["vectorField"] = query.Vector.Field,
                            ["vectorDistanceFunction"] = fieldPolicy.DistanceFunction
                        }
                    }
                });
            }
        }

        var ranked = results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Record.PartitionKey)
            .ThenBy(r => r.Record.Id)
            .Take(query.Vector.Top)
            .ToList();

        var offset = PgvectorQueryBuilder.DecodeContinuationToken(query.ContinuationToken);
        var pageSize = query.Limit ?? query.Vector.Top;
        var page = ranked.Skip(offset).Take(pageSize).ToList();
        var nextToken = offset + page.Count < ranked.Count
            ? PgvectorQueryBuilder.EncodeContinuationToken(offset + page.Count) : null;

        return new RecordSearchResult { Items = page, ContinuationToken = nextToken };
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private async Task<RecordSearchResult> SearchLexicalPageAsync(string collection, QueryEnvelope query, CancellationToken ct)
    {
        var lexical = query.Lexical!;
        if (string.IsNullOrWhiteSpace(lexical.Query))
            throw new InvalidOperationException("Lexical search query is required.");

        var qb = new PgvectorQueryBuilder();
        var (sql, parameters) = qb.BuildLexicalSearchQuery(collection, query);

        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var param in parameters) cmd.Parameters.Add(param);

        var results = new List<VyralRecordMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var record = JsonSerializer.Deserialize<VyralRecord>(reader.GetString(0), JsonOptions)!;
            var score = reader.GetFloat(1);
            if (score > 0 && (lexical.MinScore == null || score >= lexical.MinScore))
            {
                results.Add(new VyralRecordMatch { Record = record, Score = score });
            }
        }

        var ranked = results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Record.PartitionKey)
            .ThenBy(r => r.Record.Id)
            .Take(lexical.Top)
            .ToList();

        var offset = PgvectorQueryBuilder.DecodeContinuationToken(query.ContinuationToken);
        var pageSize = query.Limit ?? lexical.Top;
        var page = ranked.Skip(offset).Take(pageSize).ToList();
        var nextToken = offset + page.Count < ranked.Count
            ? PgvectorQueryBuilder.EncodeContinuationToken(offset + page.Count) : null;

        return new RecordSearchResult { Items = page, ContinuationToken = nextToken };
    }

    private async Task UpsertRecordCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string collection,
        VyralRecord record,
        RecordCollectionPolicy policy,
        RecordWritePrecondition? precondition,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var existing = await GetExistingStateAsync(conn, tx, collection, record.PartitionKey, record.Id, ct);
        if (existing.Exists)
        {
            RecordWritePreconditionValidator.EnsureSatisfied(precondition, true, existing.Etag, existing.Revision);
            ApplyPortableVersion(record, existing.Revision, existing.CreatedAt, now);
            await UpdateRecordRowAsync(conn, tx, collection, record, now, ct);
        }
        else
        {
            RecordWritePreconditionValidator.EnsureSatisfied(precondition, false, null, null);
            ApplyPortableVersion(record, existingRevision: 0, existingCreatedAt: null, now);
            var inserted = await InsertRecordRowIfMissingAsync(conn, tx, collection, record, now, ct);
            if (!inserted)
            {
                existing = await GetExistingStateAsync(conn, tx, collection, record.PartitionKey, record.Id, ct);
                if (!existing.Exists)
                {
                    throw new InvalidOperationException("Record write precondition failed: concurrent record insert was not visible.");
                }

                RecordWritePreconditionValidator.EnsureSatisfied(precondition, true, existing.Etag, existing.Revision);
                ApplyPortableVersion(record, existing.Revision, existing.CreatedAt, now);
                await UpdateRecordRowAsync(conn, tx, collection, record, now, ct);
            }
        }

        var json = JsonSerializer.Serialize(record, JsonOptions);

        // Replace vectors
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM vyral_record_vectors WHERE collection = $1 AND partition_key = $2 AND record_id = $3";
            cmd.Parameters.AddWithValue(collection);
            cmd.Parameters.AddWithValue(record.PartitionKey);
            cmd.Parameters.AddWithValue(record.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (record.Vectors != null)
        {
            foreach (var (name, vector) in record.Vectors)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO vyral_record_vectors (collection, partition_key, record_id, vector_name, vector_data, dimensions)
                    VALUES ($1, $2, $3, $4, $5::vector, $6)";
                cmd.Parameters.AddWithValue(collection);
                cmd.Parameters.AddWithValue(record.PartitionKey);
                cmd.Parameters.AddWithValue(record.Id);
                cmd.Parameters.AddWithValue(name);
                cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Unknown)
                    .Value = $"[{string.Join(",", vector.Values)}]";
                cmd.Parameters.AddWithValue(vector.Values.Length);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        // Update metadata index
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM vyral_record_metadata_index WHERE collection = $1 AND partition_key = $2 AND record_id = $3";
            cmd.Parameters.AddWithValue(collection);
            cmd.Parameters.AddWithValue(record.PartitionKey);
            cmd.Parameters.AddWithValue(record.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var path in policy.IndexedMetadata)
        {
            var value = ExtractJsonPath(json, path);
            if (value == null) continue;
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO vyral_record_metadata_index
                    (collection, partition_key, record_id, path, value_text, value_number, value_bool, value_json)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                ON CONFLICT (collection, partition_key, record_id, path) DO UPDATE SET
                    value_text = EXCLUDED.value_text,
                    value_number = EXCLUDED.value_number,
                    value_bool = EXCLUDED.value_bool,
                    value_json = EXCLUDED.value_json";
            cmd.Parameters.AddWithValue(collection);
            cmd.Parameters.AddWithValue(record.PartitionKey);
            cmd.Parameters.AddWithValue(record.Id);
            cmd.Parameters.AddWithValue(path);
            cmd.Parameters.AddWithValue(value.TextValue as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue(value.NumberValue.HasValue ? value.NumberValue.Value : DBNull.Value);
            cmd.Parameters.AddWithValue(value.BoolValue.HasValue ? value.BoolValue.Value : DBNull.Value);
            cmd.Parameters.AddWithValue(value.JsonValue);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static void ApplyPortableVersion(
        VyralRecord record,
        int existingRevision,
        DateTime? existingCreatedAt,
        DateTime now)
    {
        record.UpdatedAt = now;
        record.CreatedAt = existingCreatedAt ?? record.CreatedAt ?? now;
        record.Revision = existingRevision + 1;
        record.Etag = $"rev:{record.Revision}";
    }

    private static async Task<bool> InsertRecordRowIfMissingAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string collection,
        VyralRecord record,
        DateTime updatedAt,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO vyral_records (collection, partition_key, id, content_json, etag, revision, updated_at)
            VALUES ($1, $2, $3, $4::jsonb, $5, $6, $7)
            ON CONFLICT (collection, partition_key, id) DO NOTHING
            RETURNING 1";
        cmd.Parameters.AddWithValue(collection);
        cmd.Parameters.AddWithValue(record.PartitionKey);
        cmd.Parameters.AddWithValue(record.Id);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(record, JsonOptions));
        cmd.Parameters.AddWithValue(record.Etag!);
        cmd.Parameters.AddWithValue(record.Revision!.Value);
        cmd.Parameters.AddWithValue(updatedAt);
        var inserted = await cmd.ExecuteScalarAsync(ct);
        return inserted != null && inserted != DBNull.Value;
    }

    private static async Task UpdateRecordRowAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string collection,
        VyralRecord record,
        DateTime updatedAt,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            UPDATE vyral_records
            SET content_json = $4::jsonb,
                etag = $5,
                revision = $6,
                updated_at = $7
            WHERE collection = $1 AND partition_key = $2 AND id = $3";
        cmd.Parameters.AddWithValue(collection);
        cmd.Parameters.AddWithValue(record.PartitionKey);
        cmd.Parameters.AddWithValue(record.Id);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(record, JsonOptions));
        cmd.Parameters.AddWithValue(record.Etag!);
        cmd.Parameters.AddWithValue(record.Revision!.Value);
        cmd.Parameters.AddWithValue(updatedAt);
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        if (affected != 1)
        {
            throw new InvalidOperationException("Record write precondition failed: record changed before update.");
        }
    }

    private async Task<(bool Exists, int Revision, string? Etag, DateTime? CreatedAt)> GetExistingStateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        string collection, string partitionKey, string id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT revision, etag, content_json FROM vyral_records WHERE collection = $1 AND partition_key = $2 AND id = $3 FOR UPDATE";
        cmd.Parameters.AddWithValue(collection);
        cmd.Parameters.AddWithValue(partitionKey);
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (false, 0, null, null);

        var revision = reader.IsDBNull(0) ? 0 : checked((int)reader.GetInt64(0));
        var etag = reader.IsDBNull(1) ? null : reader.GetString(1);
        DateTime? createdAt = null;
        if (!reader.IsDBNull(2))
        {
            var existing = JsonSerializer.Deserialize<VyralRecord>(reader.GetString(2), JsonOptions);
            etag = existing?.Etag ?? etag;
            createdAt = existing?.CreatedAt;
        }

        return (true, revision, etag, createdAt);
    }

    private static IndexedValue? ExtractJsonPath(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var segments = path.TrimStart('/').Split('/');
            var el = doc.RootElement;
            foreach (var seg in segments)
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(seg, out el))
                    return null;
            }

            var rawJson = el.GetRawText();
            return el.ValueKind switch
            {
                JsonValueKind.String => new IndexedValue { TextValue = el.GetString(), JsonValue = rawJson },
                JsonValueKind.Number when el.TryGetInt64(out var i) => new IndexedValue { NumberValue = i, JsonValue = rawJson },
                JsonValueKind.Number => new IndexedValue { NumberValue = el.GetDouble(), JsonValue = rawJson },
                JsonValueKind.True => new IndexedValue { BoolValue = true, JsonValue = rawJson },
                JsonValueKind.False => new IndexedValue { BoolValue = false, JsonValue = rawJson },
                JsonValueKind.Null => new IndexedValue { JsonValue = "null" },
                _ => new IndexedValue { JsonValue = rawJson }
            };
        }
        catch
        {
            return null;
        }
    }

    private static void ValidateCollectionPolicy(RecordCollectionPolicy policy)
    {
        RecordIdentityValidator.ValidateCollectionName(policy.Name);
        if (!string.Equals(policy.PartitionKeyPath, "/partitionKey", StringComparison.Ordinal))
            throw new InvalidOperationException("Collection partition key path must be '/partitionKey'.");

        foreach (var vp in policy.VectorPolicies)
        {
            if (string.IsNullOrWhiteSpace(vp.Name))
                throw new InvalidOperationException("Vector policy name is required.");
            var expectedPath = $"/vectors/{vp.Name}/values";
            if (!string.Equals(vp.Path, expectedPath, StringComparison.Ordinal))
                throw new InvalidOperationException($"Vector policy '{vp.Name}' path must be '{expectedPath}'.");
            if (vp.Dimensions <= 0)
                throw new InvalidOperationException($"Vector policy '{vp.Name}' dimensions must be greater than zero.");
            if (!string.Equals(vp.Datatype, "float32", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Vector policy '{vp.Name}' datatype '{vp.Datatype}' is not supported by the pgvector adapter.");
        }
    }

    protected virtual async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        if (_dataSource != null)
            return await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var conn = new NpgsqlConnection(_connectionString!);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var arg in args) cmd.Parameters.AddWithValue(arg);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed class IndexedValue
    {
        public string? TextValue { get; init; }
        public double? NumberValue { get; init; }
        public bool? BoolValue { get; init; }
        public string JsonValue { get; init; } = "null";
    }
}
