using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

public class SqliteRecordCollectionStore : IRecordCollectionStore
{
    private const string SupportedPartitionKeyPath = "/partitionKey";
    // FTS5 phrase matching is positional. This indexed token occupies one position between
    // independent JSON scalar leaves, so a required phrase cannot bridge array/object values.
    // It exceeds the 256-character required-phrase contract, making it unrepresentable in
    // a required phrase while still remaining a single unicode61 token.
    private static readonly string LexicalValueBoundaryToken = new string('x', 257);
    private readonly string _connectionString;
    private readonly string _dbPath;
    private static readonly HashSet<string> SupportedDatatypes = new(StringComparer.OrdinalIgnoreCase) { VectorDatatypes.Float32 };
    private static readonly HashSet<string> SupportedDistanceFunctions = new(StringComparer.OrdinalIgnoreCase) { DistanceFunctions.Cosine, DistanceFunctions.DotProduct, DistanceFunctions.Euclidean };
    private static readonly HashSet<string> SupportedIndexTypes = new(StringComparer.OrdinalIgnoreCase) { IndexTypes.Flat, IndexTypes.QuantizedFlat, IndexTypes.DiskAnn };
    private static readonly HashSet<string> LexicalStopWords = new(StringComparer.Ordinal)
    {
        "a",
        "an",
        "and",
        "are",
        "as",
        "at",
        "be",
        "by",
        "for",
        "from",
        "in",
        "is",
        "it",
        "of",
        "on",
        "or",
        "that",
        "the",
        "to",
        "with"
    };

    public SqliteRecordCollectionStore(string dbPath)
    {
        _dbPath = dbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var migrationManager = new SqliteMigrationManager(_connectionString);
        await migrationManager.MigrateAsync(ct);
        
        using var connection = await OpenConnectionAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SqliteStorageDiagnostics> GetStorageDiagnosticsAsync(CancellationToken ct = default)
    {
        using var connection = await OpenConnectionAsync(ct);
        var quickCheck = await ExecuteScalarStringAsync(connection, "PRAGMA quick_check;", ct);
        var foreignKeyViolationCount = await CountRowsAsync(connection, "PRAGMA foreign_key_check;", ct);
        var journalMode = await ExecuteScalarStringAsync(connection, "PRAGMA journal_mode;", ct);
        var pageSize = await ExecuteScalarLongAsync(connection, "PRAGMA page_size;", ct);
        var pageCount = await ExecuteScalarLongAsync(connection, "PRAGMA page_count;", ct);
        var freelistCount = await ExecuteScalarLongAsync(connection, "PRAGMA freelist_count;", ct);
        var databaseExists = TryGetFileExists(_dbPath);
        var databaseBytes = TryGetFileLength(_dbPath);
        var walBytes = TryGetFileLength(_dbPath + "-wal");
        var shmBytes = TryGetFileLength(_dbPath + "-shm");

        return new SqliteStorageDiagnostics
        {
            Healthy = string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase) && foreignKeyViolationCount == 0,
            QuickCheck = quickCheck,
            ForeignKeyViolationCount = foreignKeyViolationCount,
            JournalMode = journalMode,
            PageSize = pageSize,
            PageCount = pageCount,
            FreelistCount = freelistCount,
            DatabaseExists = databaseExists,
            DatabaseBytes = databaseExists == true ? databaseBytes : null,
            WalBytes = walBytes,
            ShmBytes = shmBytes
        };
    }

    public async Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default)
    {
        ValidateCollectionPolicy(policy);

        var existingPolicy = await GetCollectionPolicyAsync(policy.Name, ct);
        if (existingPolicy != null)
        {
            if (!RecordCollectionPolicyComparer.AreEquivalent(existingPolicy, policy))
            {
                throw new InvalidOperationException($"Collection '{policy.Name}' already exists with a different policy.");
            }

            return;
        }

        using var connection = await OpenConnectionAsync(ct);

        var json = JsonSerializer.Serialize(policy);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO vyral_collections (name, policy_json)
            VALUES ($name, $json);";
        command.Parameters.AddWithValue("$name", policy.Name);
        command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync(ct);

    }

    public async Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default)
    {
        using var connection = await OpenConnectionAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM vyral_collections ORDER BY name;";

        var results = new List<string>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    public async Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(string collection, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);

        using var connection = await OpenConnectionAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT policy_json FROM vyral_collections WHERE name = $name;";
        command.Parameters.AddWithValue("$name", collection);

        using var reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return JsonSerializer.Deserialize<RecordCollectionPolicy>(reader.GetString(0));
        }
        return null;
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);

        using var connection = await OpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM vyral_record_fts WHERE collection = $collection;";
                command.Parameters.AddWithValue("$collection", collection);
                await command.ExecuteNonQueryAsync(ct);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM vyral_record_metadata_index WHERE collection = $collection;";
                command.Parameters.AddWithValue("$collection", collection);
                await command.ExecuteNonQueryAsync(ct);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM vyral_record_vectors WHERE collection = $collection;";
                command.Parameters.AddWithValue("$collection", collection);
                await command.ExecuteNonQueryAsync(ct);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM vyral_records WHERE collection = $collection;";
                command.Parameters.AddWithValue("$collection", collection);
                await command.ExecuteNonQueryAsync(ct);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM vyral_collections WHERE name = $collection;";
                command.Parameters.AddWithValue("$collection", collection);
                await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default)
    {
        await UpsertRecordAsync(collection, record, precondition: null, ct);
    }

    public async Task UpsertRecordAsync(string collection, VyralRecord record, RecordWritePrecondition? precondition, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidateRecord(record);

        var policy = await GetCollectionPolicyAsync(collection, ct);
        if (policy == null) throw new InvalidOperationException($"Collection '{collection}' does not exist.");

        RecordVectorValidator.ValidateRecordVectors(collection, policy, record);

        using var connection = await OpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            var (exists, existingRevision, existingEtag, existingCreatedAt) = await GetExistingRecordStateAsync(connection, transaction, collection, record.PartitionKey, record.Id, ct);
            RecordWritePreconditionValidator.EnsureSatisfied(precondition, exists, existingEtag, existingRevision);
            var now = DateTime.UtcNow;
            record.UpdatedAt = now;
            record.CreatedAt = existingCreatedAt ?? record.CreatedAt ?? now;
            record.Revision = existingRevision + 1;
            record.Etag = $"rev:{record.Revision}";

            var json = JsonSerializer.Serialize(record);

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO vyral_records (collection, partitionKey, id, content_json, etag, revision, updated_at)
                    VALUES ($collection, $partitionKey, $id, $json, $etag, $revision, $updated_at)
                    ON CONFLICT(collection, partitionKey, id) DO UPDATE SET
                        content_json = excluded.content_json,
                        etag = excluded.etag,
                        revision = excluded.revision,
                        updated_at = excluded.updated_at;";
                command.Parameters.AddWithValue("$collection", collection);
                command.Parameters.AddWithValue("$partitionKey", record.PartitionKey);
                command.Parameters.AddWithValue("$id", record.Id);
                command.Parameters.AddWithValue("$json", json);
                command.Parameters.AddWithValue("$etag", record.Etag);
                command.Parameters.AddWithValue("$revision", record.Revision);
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
                await command.ExecuteNonQueryAsync(ct);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
                    DELETE FROM vyral_record_vectors
                    WHERE collection = $collection AND partitionKey = $partitionKey AND record_id = $record_id;";
                command.Parameters.AddWithValue("$collection", collection);
                command.Parameters.AddWithValue("$partitionKey", record.PartitionKey);
                command.Parameters.AddWithValue("$record_id", record.Id);
                await command.ExecuteNonQueryAsync(ct);
            }

            if (record.Vectors != null)
            {
                foreach (var (name, vector) in record.Vectors)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
                        INSERT INTO vyral_record_vectors (collection, partitionKey, record_id, vector_name, vector_data, dimensions)
                        VALUES ($collection, $partitionKey, $record_id, $name, $data, $dimensions);";
                    command.Parameters.AddWithValue("$collection", collection);
                    command.Parameters.AddWithValue("$partitionKey", record.PartitionKey);
                    command.Parameters.AddWithValue("$record_id", record.Id);
                    command.Parameters.AddWithValue("$name", name);
                    
                    var byteBuffer = new byte[vector.Values.Length * 4];
                    Buffer.BlockCopy(vector.Values, 0, byteBuffer, 0, byteBuffer.Length);
                    command.Parameters.AddWithValue("$data", byteBuffer);
                    command.Parameters.AddWithValue("$dimensions", vector.Values.Length);
                    await command.ExecuteNonQueryAsync(ct);
                }
            }

            await UpdateMetadataIndexAsync(connection, transaction, collection, record, policy.IndexedMetadata, ct);
            await UpdateLexicalIndexAsync(connection, transaction, collection, record, ct);

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<VyralRecord?> GetRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidatePartitionKey(partitionKey);
        RecordIdentityValidator.ValidateRecordId(id);

        using var connection = await OpenConnectionAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_json FROM vyral_records WHERE collection = $collection AND partitionKey = $partitionKey AND id = $id;";
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$partitionKey", partitionKey);
        command.Parameters.AddWithValue("$id", id);

        using var reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return JsonSerializer.Deserialize<VyralRecord>(reader.GetString(0));
        }
        return null;
    }

    public async Task DeleteRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidatePartitionKey(partitionKey);
        RecordIdentityValidator.ValidateRecordId(id);

        using var connection = await OpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM vyral_record_fts WHERE collection = $collection AND partitionKey = $partitionKey AND record_id = $id;";
                command.Parameters.AddWithValue("$collection", collection);
                command.Parameters.AddWithValue("$partitionKey", partitionKey);
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(ct);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM vyral_record_metadata_index WHERE collection = $collection AND partitionKey = $partitionKey AND record_id = $id;";
                command.Parameters.AddWithValue("$collection", collection);
                command.Parameters.AddWithValue("$partitionKey", partitionKey);
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(ct);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM vyral_record_vectors WHERE collection = $collection AND partitionKey = $partitionKey AND record_id = $id;";
                command.Parameters.AddWithValue("$collection", collection);
                command.Parameters.AddWithValue("$partitionKey", partitionKey);
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(ct);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM vyral_records WHERE collection = $collection AND partitionKey = $partitionKey AND id = $id;";
                command.Parameters.AddWithValue("$collection", collection);
                command.Parameters.AddWithValue("$partitionKey", partitionKey);
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<RecordBatchUpsertResult> UpsertRecordsAsync(string collection, RecordBatchUpsertRequest request, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        request.ValidatePreconditionAlignment();
        var itemResults = new List<RecordBatchUpsertItemResult>(request.Records.Count);
        int succeeded = 0, failed = 0;
        bool stopped = false;

        for (int i = 0; i < request.Records.Count; i++)
        {
            var record = request.Records[i];
            try
            {
                await UpsertRecordAsync(collection, record, request.GetPrecondition(i), ct);
                itemResults.Add(new RecordBatchUpsertItemResult
                {
                    Index = i, Id = record.Id, PartitionKey = record.PartitionKey,
                    Status = RecordUpsertStatuses.Succeeded, Etag = record.Etag, Revision = record.Revision
                });
                succeeded++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                itemResults.Add(new RecordBatchUpsertItemResult
                {
                    Index = i, Id = record.Id, PartitionKey = record.PartitionKey,
                    Status = RecordUpsertStatuses.Failed, Error = ex.Message
                });
                failed++;
                if (!request.ContinueOnError) { stopped = i + 1 < request.Records.Count; break; }
            }
        }

        return new RecordBatchUpsertResult
        {
            Collection = collection, Requested = request.Records.Count,
            Attempted = succeeded + failed, Succeeded = succeeded,
            Failed = failed, StoppedOnError = stopped, Items = itemResults
        };
    }

    public async Task<RecordQueryResult> QueryRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        ValidatePageLimit(query.Limit, "Query page size");

        var policy = await GetCollectionPolicyAsync(collection, ct);
        if (policy == null) throw new InvalidOperationException($"Collection '{collection}' does not exist.");

        var queryBuilder = new SqliteQueryBuilder();
        var offset = DecodeContinuationToken(query.ContinuationToken);
        var pageSize = query.Limit;
        var queryToExecute = CloneQuery(query);
        if (pageSize.HasValue)
        {
            queryToExecute.Limit = pageSize.Value + 1;
        }

        var (sql, parameters) = queryBuilder.BuildQuery(collection, queryToExecute, policy.IndexedMetadata);

        using var connection = await OpenConnectionAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var param in parameters) command.Parameters.Add(param);

        var results = new List<VyralRecord>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(JsonSerializer.Deserialize<VyralRecord>(reader.GetString(0))!);
        }
        return BuildRecordQueryResult(results, pageSize, offset);
    }

    public async Task<RecordSearchResult> SearchRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        if (query.Vector == null)
        {
            if (query.Lexical != null)
            {
                return await SearchLexicalRecordsPageAsync(collection, query, ct);
            }

            var records = await QueryRecordsPageAsync(collection, query, ct);
            return new RecordSearchResult
            {
                Items = records.Items.Select(r => new VyralRecordMatch { Record = r, Score = 1.0f }).ToList(),
                ContinuationToken = records.ContinuationToken
            };
        }

        if (query.Lexical != null)
        {
            throw new InvalidOperationException("Combined vector and lexical search is provided by the retrieval service; collection search accepts one search mode at a time.");
        }

        // 1. Get policy
        var policy = await GetCollectionPolicyAsync(collection, ct);
        if (policy == null) throw new InvalidOperationException($"Collection '{collection}' does not exist.");

        var fieldPolicy = RecordVectorValidator.ValidateSearchVector(collection, policy, query.Vector);
        ValidatePageLimit(query.Limit, "Search page size");

        using var connection = await OpenConnectionAsync(ct);

        var results = new List<VyralRecordMatch>();

        var queryBuilder = new SqliteQueryBuilder();
        var (sql, parameters) = queryBuilder.BuildVectorCandidateQuery(collection, query, policy.IndexedMetadata);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.Add(parameter);

        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var record = JsonSerializer.Deserialize<VyralRecord>(reader.GetString(0))!;
            var vectorBytes = (byte[])reader.GetValue(1);
            var storedDimensions = reader.GetInt32(2);
            if (storedDimensions != fieldPolicy.Dimensions)
            {
                throw new InvalidOperationException($"Stored vector for record '{record.Id}' has dimensions {storedDimensions}, but policy expects {fieldPolicy.Dimensions}.");
            }
            var vectorValues = new float[vectorBytes.Length / 4];
            Buffer.BlockCopy(vectorBytes, 0, vectorValues, 0, vectorBytes.Length);

            var score = CalculateSimilarityScore(fieldPolicy.DistanceFunction, query.Vector.Value, vectorValues);
            if (query.Vector.MinScore == null || score >= query.Vector.MinScore)
            {
                results.Add(new VyralRecordMatch
                {
                    Record = record,
                    Score = score,
                    Diagnostics = new RetrievalDiagnostics
                    {
                        ResultIdentity = BuildSearchResultIdentity(collection, record),
                        CandidateSources = new List<string> { "vector" },
                        ReasonCodes = new List<string>
                        {
                            "result.identity.record",
                            "mode.vector",
                            "candidate.source.vector",
                            "score.vector.raw_similarity"
                        },
                        ScoreNormalization = BuildVectorSearchScoreNormalization(fieldPolicy.DistanceFunction),
                        ScoreComponents = new Dictionary<string, float>
                        {
                            ["vector"] = score
                        },
                        Details = new Dictionary<string, object?>
                        {
                            ["vectorField"] = query.Vector.Field,
                            ["vectorDistanceFunction"] = fieldPolicy.DistanceFunction,
                            ["vectorIndexProvider"] = "sqlite-flat-scan",
                            ["vectorIndexUsed"] = false,
                            ["vectorIndexAvailable"] = false,
                            ["vectorIndexQuantized"] = false,
                            ["vectorIndexReason"] = "local_exact_scan"
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
        AddSearchTieBreakDiagnostics(ranked, collection, "vector", results.Count);

        var offset = DecodeContinuationToken(query.ContinuationToken);
        var pageSize = query.Limit ?? query.Vector.Top;

        var pageItems = ranked.Skip(offset).Take(pageSize).ToList();
        var next = offset + pageItems.Count < ranked.Count ? EncodeContinuationToken(offset + pageItems.Count) : null;
        return new RecordSearchResult { Items = pageItems, ContinuationToken = next };
    }

    private async Task<RecordSearchResult> SearchLexicalRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct)
    {
        var lexical = query.Lexical ?? throw new InvalidOperationException("Lexical search options are required.");
        if (string.IsNullOrWhiteSpace(lexical.Query))
        {
            throw new InvalidOperationException("Lexical search query is required.");
        }

        ValidatePageLimit(lexical.Top, "Lexical top");
        ValidatePageLimit(lexical.ScanLimit, "Lexical scan limit");
        ValidatePageLimit(query.Limit, "Search page size");

        var policy = await GetCollectionPolicyAsync(collection, ct);
        if (policy == null) throw new InvalidOperationException($"Collection '{collection}' does not exist.");

        var candidateQuery = CloneQuery(query);
        candidateQuery.Vector = null;
        candidateQuery.Lexical = null;
        candidateQuery.OrderBy = null;
        candidateQuery.ContinuationToken = null;
        candidateQuery.Limit = lexical.ScanLimit;

        var candidateSource = "scan";
        var requiredPhraseGroups = LocalLexicalScorer.NormalizeRequiredPhraseGroups(lexical.RequiredPhraseGroups);
        var ftsExpression = BuildFtsMatchExpression(
            lexical.Query,
            lexical.MatchMode,
            lexical.PrefixMatching,
            lexical.PrefixMinChars,
            requiredPhraseGroups);
        var records = string.IsNullOrWhiteSpace(ftsExpression)
            ? (await QueryRecordsPageAsync(collection, candidateQuery, ct)).Items
            : await QueryLexicalFtsCandidatesAsync(collection, candidateQuery, policy, ftsExpression, ct);
        if (!string.IsNullOrWhiteSpace(ftsExpression))
        {
            candidateSource = "sqlite_fts5";
        }

        var results = new List<VyralRecordMatch>();
        foreach (var scored in LocalLexicalScorer.ScoreMany(records, lexical.Query, lexical, ct))
        {
            ct.ThrowIfCancellationRequested();
            var score = scored.Score;
            if (score.Score <= 0)
            {
                continue;
            }

            if (lexical.MinScore.HasValue && score.Score < lexical.MinScore.Value)
            {
                continue;
            }

            var diagnostics = score.ToDiagnostics("lexical");
            diagnostics.ResultIdentity = BuildSearchResultIdentity(collection, scored.Record);
            diagnostics.ReasonCodes = new List<string>
            {
                "result.identity.record",
                "mode.lexical",
                "candidate.source.lexical",
                "score.lexical"
            };
            diagnostics.ScoreNormalization = BuildLexicalSearchScoreNormalization(diagnostics);
            diagnostics.Details["lexicalCandidateSource"] = candidateSource;
            diagnostics.Details["lexicalCandidateCount"] = records.Count;
            if (!string.IsNullOrWhiteSpace(ftsExpression))
            {
                diagnostics.Details["lexicalFtsExpression"] = ftsExpression;
            }
            if (requiredPhraseGroups.Count > 0)
            {
                diagnostics.Details["requiredPhraseGroups"] = requiredPhraseGroups;
            }

            results.Add(new VyralRecordMatch
            {
                Record = scored.Record,
                Score = score.Score,
                Diagnostics = diagnostics
            });
        }

        var ranked = results
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Record.PartitionKey)
            .ThenBy(result => result.Record.Id)
            .Take(lexical.Top)
            .ToList();
        AddSearchTieBreakDiagnostics(ranked, collection, "lexical", records.Count);

        var offset = DecodeContinuationToken(query.ContinuationToken);
        var pageSize = query.Limit ?? lexical.Top;
        var pageItems = ranked.Skip(offset).Take(pageSize).ToList();
        var next = offset + pageItems.Count < ranked.Count ? EncodeContinuationToken(offset + pageItems.Count) : null;
        return new RecordSearchResult { Items = pageItems, ContinuationToken = next };
    }

    private async Task<List<VyralRecord>> QueryLexicalFtsCandidatesAsync(
        string collection,
        QueryEnvelope candidateQuery,
        RecordCollectionPolicy policy,
        string ftsExpression,
        CancellationToken ct)
    {
        var queryBuilder = new SqliteQueryBuilder();
        var (sql, parameters) = queryBuilder.BuildLexicalFtsCandidateQuery(collection, candidateQuery, ftsExpression, policy.IndexedMetadata);

        using var connection = await OpenConnectionAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var records = new List<VyralRecord>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            records.Add(JsonSerializer.Deserialize<VyralRecord>(reader.GetString(0))!);
        }

        return records;
    }

    private static void AddSearchTieBreakDiagnostics(List<VyralRecordMatch> ranked, string collection, string searchMode, int candidateCount)
    {
        for (var index = 0; index < ranked.Count; index++)
        {
            var match = ranked[index];
            match.Diagnostics ??= new RetrievalDiagnostics();
            match.Diagnostics.ResultIdentity ??= BuildSearchResultIdentity(collection, match.Record);
            match.Diagnostics.CandidateCounts["searchCandidatePool"] = candidateCount;
            match.Diagnostics.CandidateCounts["returnedCandidates"] = ranked.Count;
            match.Diagnostics.Details["rank"] = index + 1;
            match.Diagnostics.Details["tieBreakOrder"] = "score desc, partitionKey asc, id asc";
            match.Diagnostics.Details["tieBreakKey"] = new Dictionary<string, object?>
            {
                ["score"] = match.Score,
                ["partitionKey"] = match.Record.PartitionKey,
                ["id"] = match.Record.Id
            };
            AddSearchReasonCode(match.Diagnostics, $"mode.{searchMode}");
            AddSearchReasonCode(match.Diagnostics, "rank.final.assigned");
            AddSearchReasonCode(match.Diagnostics, "rank.tie_break.applied");
        }
    }

    private static RetrievalResultIdentity BuildSearchResultIdentity(string collection, VyralRecord record)
    {
        return new RetrievalResultIdentity
        {
            Collection = collection,
            PartitionKey = record.PartitionKey,
            Id = record.Id,
            Type = record.Type,
            Etag = record.Etag,
            Revision = record.Revision
        };
    }

    private static RetrievalScoreNormalization BuildVectorSearchScoreNormalization(string distanceFunction)
    {
        return new RetrievalScoreNormalization
        {
            FinalScoreKind = "vector.raw_similarity",
            VectorScoreKind = $"vector.similarity.{NormalizeSearchScoreKindToken(distanceFunction)}",
            VectorDistanceFunction = distanceFunction,
            VectorNormalization = DescribeSearchVectorNormalization(distanceFunction)
        };
    }

    private static RetrievalScoreNormalization BuildLexicalSearchScoreNormalization(RetrievalDiagnostics diagnostics)
    {
        var lexicalKind = "score";
        if (diagnostics.Details.TryGetValue("lexicalScoring", out var scoring))
        {
            lexicalKind = NormalizeSearchScoreKindToken(ObjectToText(scoring));
        }

        return new RetrievalScoreNormalization
        {
            FinalScoreKind = "lexical.score",
            LexicalScoreKind = $"lexical.{lexicalKind}"
        };
    }

    private static string? DescribeSearchVectorNormalization(string? distanceFunction)
    {
        return NormalizeSearchScoreKindToken(distanceFunction) switch
        {
            "cosine" => "clamp((score+1)/2,0,1)",
            "euclidean" => "clamp(local_similarity,0,1)",
            "dotproduct" => "score<=0?0:score/(1+score)",
            "" => null,
            _ => "clamp(score,0,1)"
        };
    }

    private static string NormalizeSearchScoreKindToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var token = value.Trim().ToLowerInvariant();
        return token switch
        {
            "dot product" => "dotproduct",
            "dot_product" => "dotproduct",
            "dot-product" => "dotproduct",
            _ => token.Replace(" ", "_", StringComparison.Ordinal).Replace("-", "_", StringComparison.Ordinal)
        };
    }

    private static string ObjectToText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonElement element => element.ToString(),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static void AddSearchReasonCode(RetrievalDiagnostics diagnostics, string reasonCode)
    {
        if (!diagnostics.ReasonCodes.Contains(reasonCode, StringComparer.Ordinal))
        {
            diagnostics.ReasonCodes.Add(reasonCode);
        }
    }

    private static float CalculateSimilarityScore(string distanceFunction, float[] queryVector, float[] storedVector)
    {
        return distanceFunction.ToLowerInvariant() switch
        {
            "cosine" => CosineSimilarity(queryVector, storedVector),
            "dotproduct" => DotProductSimilarity(queryVector, storedVector),
            "euclidean" => EuclideanSimilarity(queryVector, storedVector),
            _ => throw new InvalidOperationException($"Vector distance function '{distanceFunction}' is not supported.")
        };
    }

    private static float CosineSimilarity(float[] v1, float[] v2)
    {
        ValidateVectorLengths(v1, v2);
        return TensorPrimitives.CosineSimilarity(v1.AsSpan(), v2.AsSpan());
    }

    private static float DotProductSimilarity(float[] v1, float[] v2)
    {
        ValidateVectorLengths(v1, v2);
        return TensorPrimitives.Dot(v1.AsSpan(), v2.AsSpan());
    }

    private static float EuclideanSimilarity(float[] v1, float[] v2)
    {
        ValidateVectorLengths(v1, v2);
        var distance = TensorPrimitives.Distance(v1.AsSpan(), v2.AsSpan());
        return 1.0f / (1.0f + distance);
    }

    private static void ValidateVectorLengths(float[] v1, float[] v2)
    {
        if (v1.Length != v2.Length)
        {
            throw new InvalidOperationException($"Vector dimensions differ: {v1.Length} != {v2.Length}.");
        }
    }

    private static RecordQueryResult BuildRecordQueryResult(List<VyralRecord> records, int? pageSize, int offset)
    {
        if (!pageSize.HasValue)
        {
            return new RecordQueryResult { Items = records };
        }

        var items = records.Take(pageSize.Value).ToList();
        var next = records.Count > pageSize.Value ? EncodeContinuationToken(offset + pageSize.Value) : null;
        return new RecordQueryResult { Items = items, ContinuationToken = next };
    }

    private static void ValidatePageLimit(int? limit, string description)
    {
        if (limit <= 0)
        {
            throw new InvalidOperationException($"{description} must be greater than zero.");
        }
    }

    private static QueryEnvelope CloneQuery(QueryEnvelope query)
    {
        return new QueryEnvelope
        {
            PartitionKeys = query.PartitionKeys == null ? null : new List<string>(query.PartitionKeys),
            Filter = query.Filter,
            Vector = query.Vector,
            Lexical = query.Lexical,
            OrderBy = query.OrderBy == null ? null : new List<OrderExpression>(query.OrderBy),
            Limit = query.Limit,
            ContinuationToken = query.ContinuationToken
        };
    }

    private static string EncodeContinuationToken(int offset)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));
    }

    private static int DecodeContinuationToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var offset = int.Parse(decoded, CultureInfo.InvariantCulture);
            if (offset < 0) throw new InvalidOperationException("Continuation token offset must be non-negative.");
            return offset;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Continuation token is not valid for the local SQLite adapter.", ex);
        }
    }

    private static async Task<string> ExecuteScalarStringAsync(SqliteConnection connection, string commandText, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<long> ExecuteScalarLongAsync(SqliteConnection connection, string commandText, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountRowsAsync(SqliteConnection connection, string commandText, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        using var reader = await command.ExecuteReaderAsync(ct);
        var count = 0;
        while (await reader.ReadAsync(ct))
        {
            count++;
        }

        return count;
    }

    private static bool? TryGetFileExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.Equals(path, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return File.Exists(path);
        }
        catch
        {
            return null;
        }
    }

    private static long? TryGetFileLength(string path)
    {
        var exists = TryGetFileExists(path);
        if (exists is null)
        {
            return null;
        }

        if (!exists.Value)
        {
            return 0;
        }

        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return null;
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);


        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(ct);

        command.CommandText = "PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(ct);

        return connection;
    }

    private static void ValidateCollectionPolicy(RecordCollectionPolicy policy)
    {
        RecordIdentityValidator.ValidateCollectionName(policy.Name);

        if (!string.Equals(policy.PartitionKeyPath, SupportedPartitionKeyPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Collection partition key path must be '{SupportedPartitionKeyPath}'.");
        }

        if (policy.VectorPolicies.GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw new InvalidOperationException("Vector policy names must be unique within a collection.");
        }

        if (policy.VectorPolicies.GroupBy(p => p.Path, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw new InvalidOperationException("Vector policy paths must be unique within a collection.");
        }

        if (policy.IndexedMetadata.GroupBy(path => path, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw new InvalidOperationException("Indexed metadata paths must be unique within a collection.");
        }

        foreach (var path in policy.IndexedMetadata)
        {
            ValidateJsonPointer(path, "Indexed metadata path");
        }

        foreach (var vectorPolicy in policy.VectorPolicies)
        {
            if (string.IsNullOrWhiteSpace(vectorPolicy.Name)) throw new InvalidOperationException("Vector policy name is required.");
            if (!IsSupportedPathSegment(vectorPolicy.Name)) throw new InvalidOperationException($"Vector policy name '{vectorPolicy.Name}' contains unsupported characters.");
            if (!string.Equals(vectorPolicy.Path, BuildExpectedVectorPath(vectorPolicy.Name), StringComparison.Ordinal))
                throw new InvalidOperationException($"Vector policy '{vectorPolicy.Name}' path must be '{BuildExpectedVectorPath(vectorPolicy.Name)}'.");
            if (vectorPolicy.Dimensions <= 0) throw new InvalidOperationException($"Vector policy '{vectorPolicy.Name}' dimensions must be greater than zero.");
            if (!SupportedDatatypes.Contains(vectorPolicy.Datatype)) throw new InvalidOperationException($"Vector policy '{vectorPolicy.Name}' datatype '{vectorPolicy.Datatype}' is not supported.");
            if (!SupportedDistanceFunctions.Contains(vectorPolicy.DistanceFunction)) throw new InvalidOperationException($"Vector policy '{vectorPolicy.Name}' distance function '{vectorPolicy.DistanceFunction}' is not supported.");
            if (!SupportedIndexTypes.Contains(vectorPolicy.IndexType)) throw new InvalidOperationException($"Vector policy '{vectorPolicy.Name}' index type '{vectorPolicy.IndexType}' is not supported.");
        }
    }

    private static async Task<(bool Exists, int Revision, string? Etag, DateTime? CreatedAt)> GetExistingRecordStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string collection,
        string partitionKey,
        string id,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT content_json, revision FROM vyral_records
            WHERE collection = $collection AND partitionKey = $partitionKey AND id = $id;";
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$partitionKey", partitionKey);
        command.Parameters.AddWithValue("$id", id);

        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return (false, 0, null, null);
        }

        var revision = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        var existingRecord = JsonSerializer.Deserialize<VyralRecord>(reader.GetString(0));
        return (true, revision, existingRecord?.Etag, existingRecord?.CreatedAt);
    }

    private static async Task UpdateMetadataIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string collection,
        VyralRecord record,
        IEnumerable<string> indexedMetadata,
        CancellationToken ct)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = @"
                DELETE FROM vyral_record_metadata_index
                WHERE collection = $collection AND partitionKey = $partitionKey AND record_id = $record_id;";
            delete.Parameters.AddWithValue("$collection", collection);
            delete.Parameters.AddWithValue("$partitionKey", record.PartitionKey);
            delete.Parameters.AddWithValue("$record_id", record.Id);
            await delete.ExecuteNonQueryAsync(ct);
        }

        var entries = BuildMetadataIndexEntries(record, indexedMetadata).ToList();
        foreach (var entry in entries)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"
                INSERT INTO vyral_record_metadata_index (
                    collection,
                    partitionKey,
                    record_id,
                    path,
                    value_text,
                    value_number,
                    value_bool,
                    value_json
                )
                VALUES (
                    $collection,
                    $partitionKey,
                    $record_id,
                    $path,
                    $value_text,
                    $value_number,
                    $value_bool,
                    $value_json
                );";
            insert.Parameters.AddWithValue("$collection", collection);
            insert.Parameters.AddWithValue("$partitionKey", record.PartitionKey);
            insert.Parameters.AddWithValue("$record_id", record.Id);
            insert.Parameters.AddWithValue("$path", entry.Path);
            insert.Parameters.AddWithValue("$value_text", (object?)entry.ValueText ?? DBNull.Value);
            insert.Parameters.AddWithValue("$value_number", (object?)entry.ValueNumber ?? DBNull.Value);
            insert.Parameters.AddWithValue("$value_bool", entry.ValueBool.HasValue ? (entry.ValueBool.Value ? 1 : 0) : DBNull.Value);
            insert.Parameters.AddWithValue("$value_json", entry.ValueJson);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task UpdateLexicalIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string collection,
        VyralRecord record,
        CancellationToken ct)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = @"
                DELETE FROM vyral_record_fts
                WHERE collection = $collection AND partitionKey = $partitionKey AND record_id = $record_id;";
            delete.Parameters.AddWithValue("$collection", collection);
            delete.Parameters.AddWithValue("$partitionKey", record.PartitionKey);
            delete.Parameters.AddWithValue("$record_id", record.Id);
            await delete.ExecuteNonQueryAsync(ct);
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = @"
            INSERT INTO vyral_record_fts (collection, partitionKey, record_id, text)
            VALUES ($collection, $partitionKey, $record_id, $text);";
        insert.Parameters.AddWithValue("$collection", collection);
        insert.Parameters.AddWithValue("$partitionKey", record.PartitionKey);
        insert.Parameters.AddWithValue("$record_id", record.Id);
        insert.Parameters.AddWithValue("$text", BuildLexicalIndexText(record));
        await insert.ExecuteNonQueryAsync(ct);
    }

    internal static string BuildLexicalIndexText(VyralRecord record)
    {
        var values = new List<string>();
        AppendLexicalValue(values, record.Id);
        AppendLexicalValue(values, record.PartitionKey);
        AppendLexicalValue(values, record.Type);
        AppendLexicalValue(values, record.SchemaVersion);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(record));
        AppendJsonLexicalValues(document.RootElement, values);
        return string.Join($" {LexicalValueBoundaryToken} ", values);
    }

    private static void AppendJsonLexicalValues(JsonElement value, List<string> values)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                AppendLexicalValue(values, value.GetString());
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                AppendLexicalValue(values, value.ToString());
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    AppendJsonLexicalValues(item, values);
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (string.Equals(property.Name, "vectors", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    AppendJsonLexicalValues(property.Value, values);
                }
                break;
        }
    }

    private static void AppendLexicalValue(List<string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        values.Add(value);
    }

    private static string BuildFtsMatchExpression(
        string query,
        string? matchMode,
        bool prefixMatching,
        int prefixMinChars,
        IReadOnlyList<List<string>> requiredPhraseGroups)
    {
        if (prefixMinChars <= 0)
        {
            throw new InvalidOperationException("Lexical prefixMinChars must be greater than zero.");
        }

        var hasBalancedQuotes = HasBalancedQuotes(query);
        var phrases = (hasBalancedQuotes ? ExtractQuotedPhrases(query) : Enumerable.Empty<string>())
            .Select(phrase => TokenizeLexicalQuery(phrase).ToList())
            .Where(tokens => tokens.Count > 0)
            .Select(tokens => $"\"{string.Join(" ", tokens)}\"")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var residualQuery = hasBalancedQuotes ? RemoveQuotedPhrases(query) : query;
        var residualTokens = TokenizeLexicalQuery(residualQuery).ToList();
        var tokens = residualTokens.Count == 0 && phrases.Count == 0
            ? TokenizeLexicalQuery(query).ToList()
            : residualTokens;
        var terms = NormalizeLexicalTerms(tokens);

        var expressions = phrases
            .Concat(terms.Select(term => BuildFtsTermExpression(term, prefixMatching, prefixMinChars)))
            .Where(expression => !string.IsNullOrWhiteSpace(expression))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var queryExpression = expressions.Count == 0
            ? string.Empty
            : string.Join(
                string.Equals(matchMode?.Trim(), "all", StringComparison.OrdinalIgnoreCase) ? " AND " : " OR ",
                expressions);
        var requiredExpressions = requiredPhraseGroups
            .Select(group => group
                .Select(BuildFtsRequiredPhraseExpression)
                .Where(expression => !string.IsNullOrWhiteSpace(expression))
                .ToList())
            .Select(group => group.Count == 1 ? group[0] : "(" + string.Join(" OR ", group) + ")")
            .ToList();
        if (string.IsNullOrWhiteSpace(queryExpression))
        {
            return requiredExpressions.Count == 0 ? string.Empty : string.Join(" AND ", requiredExpressions);
        }

        return requiredExpressions.Count == 0
            ? queryExpression
            : "(" + queryExpression + ") AND " + string.Join(" AND ", requiredExpressions);
    }

    private static List<string> NormalizeLexicalTerms(List<string> tokens)
    {
        var significantTokens = tokens
            .Where(term => term.Length > 1 && !LexicalStopWords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return significantTokens.Count == 0
            ? tokens.Distinct(StringComparer.Ordinal).ToList()
            : significantTokens;
    }

    private static string BuildFtsTermExpression(string term, bool prefixMatching, int prefixMinChars)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return string.Empty;
        }

        return prefixMatching && term.Length >= prefixMinChars
            ? term + "*"
            : $"\"{term}\"";
    }

    private static string BuildFtsRequiredPhraseExpression(string phrase)
    {
        var tokens = TokenizeLexicalQuery(phrase).ToList();
        return tokens.Count == 0 ? string.Empty : $"\"{string.Join(" ", tokens)}\"";
    }

    private static IEnumerable<string> ExtractQuotedPhrases(string query)
    {
        var start = -1;
        for (var index = 0; index < query.Length; index++)
        {
            if (query[index] != '"')
            {
                continue;
            }

            if (start < 0)
            {
                start = index + 1;
                continue;
            }

            if (index > start)
            {
                yield return query[start..index];
            }

            start = -1;
        }
    }

    private static bool HasBalancedQuotes(string query)
    {
        var quoteCount = query.Count(c => c == '"');
        return quoteCount > 0 && quoteCount % 2 == 0;
    }

    private static string RemoveQuotedPhrases(string query)
    {
        var builder = new StringBuilder(query.Length);
        var insideQuote = false;
        foreach (var c in query)
        {
            if (c == '"')
            {
                insideQuote = !insideQuote;
                builder.Append(' ');
                continue;
            }

            builder.Append(insideQuote ? ' ' : c);
        }

        return builder.ToString();
    }

    private static IEnumerable<string> TokenizeLexicalQuery(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static IEnumerable<MetadataIndexEntry> BuildMetadataIndexEntries(VyralRecord record, IEnumerable<string> indexedMetadata)
    {
        var paths = indexedMetadata.Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0) yield break;

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(record));
        foreach (var path in paths)
        {
            if (!TryGetJsonPointerValue(document.RootElement, path, out var value)) continue;

            yield return value.ValueKind switch
            {
                JsonValueKind.String => new MetadataIndexEntry(path, value.GetString(), null, null, value.GetRawText()),
                JsonValueKind.Number when value.TryGetDouble(out var number) => new MetadataIndexEntry(path, null, number, null, value.GetRawText()),
                JsonValueKind.True => new MetadataIndexEntry(path, null, null, true, value.GetRawText()),
                JsonValueKind.False => new MetadataIndexEntry(path, null, null, false, value.GetRawText()),
                JsonValueKind.Null => new MetadataIndexEntry(path, null, null, null, value.GetRawText()),
                _ => new MetadataIndexEntry(path, null, null, null, value.GetRawText())
            };
        }
    }

    private static bool TryGetJsonPointerValue(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (!path.StartsWith("/", StringComparison.Ordinal)) return false;

        foreach (var rawSegment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateJsonPointer(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{description} must be a JSON pointer.");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => !IsSupportedPathSegment(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))))
        {
            throw new InvalidOperationException($"{description} '{path}' contains unsupported segment characters.");
        }
    }

    private static bool IsSupportedPathSegment(string segment)
    {
        return segment.Length > 0 && segment.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
    }

    private static string BuildExpectedVectorPath(string name) => $"/vectors/{name}/values";

    private sealed record MetadataIndexEntry(string Path, string? ValueText, double? ValueNumber, bool? ValueBool, string ValueJson);
}
