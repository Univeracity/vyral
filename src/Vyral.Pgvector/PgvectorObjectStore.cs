using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Pgvector;

/// <summary>
/// IObjectStore implementation backed by a vyral_objects table in PostgreSQL.
/// Content is stored as BYTEA. SHA-256 content hashes, etags, and metadata are
/// stored alongside content using the same portable key/metadata validation
/// contract as the local filesystem and Azure Blob adapters.
/// </summary>
public class PgvectorObjectStore : IObjectStore
{
    private readonly string _connectionString;

    public PgvectorObjectStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default)
    {
        ObjectMetadataValidator.ValidateUserMetadata(request.Metadata);
        ObjectNameValidator.ValidateContainer(request.Container);
        var key = ObjectNameValidator.NormalizeObjectKey(request.Key);

        // Buffer content and compute SHA-256
        using var ms = new MemoryStream();
        await request.Content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var hashBytes = SHA256.HashData(bytes);
        var contentHash = "sha256:" + Convert.ToHexString(hashBytes).ToLowerInvariant();
        var etag = $"\"{contentHash}\"";
        var now = DateTime.UtcNow;
        var metaJson = System.Text.Json.JsonSerializer.Serialize(request.Metadata ?? new Dictionary<string, string>());

        await using var conn = await OpenAsync(ct);

        // Handle If-Match / If-None-Match preconditions
        if (!string.IsNullOrWhiteSpace(request.IfMatch) || !string.IsNullOrWhiteSpace(request.IfNoneMatch))
        {
            var existing = await ReadInfoInternalAsync(conn, request.Container, key, ct);
            if (!string.IsNullOrWhiteSpace(request.IfMatch) && (existing == null || existing.Etag != request.IfMatch))
                throw new InvalidOperationException("Object write precondition failed: If-Match etag does not match.");
            if (!string.IsNullOrWhiteSpace(request.IfNoneMatch) && request.IfNoneMatch == "*" && existing != null)
                throw new InvalidOperationException("Object write precondition failed: If-None-Match * but object exists.");
            if (!string.IsNullOrWhiteSpace(request.IfNoneMatch) && request.IfNoneMatch != "*" && existing != null && existing.Etag == request.IfNoneMatch)
                throw new InvalidOperationException("Object write precondition failed: If-None-Match etag matches.");
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO vyral_objects (container, key, content, content_type, content_hash, etag, metadata_json, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8)
            ON CONFLICT (container, key) DO UPDATE SET
                content = EXCLUDED.content,
                content_type = EXCLUDED.content_type,
                content_hash = EXCLUDED.content_hash,
                etag = EXCLUDED.etag,
                metadata_json = EXCLUDED.metadata_json,
                updated_at = EXCLUDED.updated_at";
        cmd.Parameters.AddWithValue(request.Container);
        cmd.Parameters.AddWithValue(key);
        cmd.Parameters.AddWithValue(bytes);
        cmd.Parameters.AddWithValue(request.ContentType as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue(contentHash);
        cmd.Parameters.AddWithValue(etag);
        cmd.Parameters.AddWithValue(metaJson);
        cmd.Parameters.AddWithValue(now);
        await cmd.ExecuteNonQueryAsync(ct);

        return new ObjectInfo
        {
            Container = request.Container,
            Key = key,
            ContentType = request.ContentType,
            ContentLength = bytes.Length,
            Etag = etag,
            ContentHash = contentHash,
            Metadata = request.Metadata ?? new Dictionary<string, string>(),
            UpdatedAt = now
        };
    }

    public async Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        var key = ObjectNameValidator.NormalizeObjectKey(request.Key);

        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT content, content_type, content_hash, etag, metadata_json, updated_at,
                   octet_length(content) AS content_length
            FROM vyral_objects
            WHERE container = $1 AND key = $2";
        cmd.Parameters.AddWithValue(request.Container);
        cmd.Parameters.AddWithValue(key);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var bytes = (byte[])reader.GetValue(0);
        var contentType = reader.IsDBNull(1) ? null : reader.GetString(1);
        var contentHash = reader.GetString(2);
        var etag = reader.GetString(3);
        var metadata = DeserializeMetadata(reader.IsDBNull(4) ? "{}" : reader.GetString(4));
        var updatedAt = reader.GetDateTime(5);
        var length = reader.GetInt64(6);

        return new ObjectResult
        {
            Container = request.Container,
            Key = key,
            ContentType = contentType,
            ContentLength = length,
            Etag = etag,
            ContentHash = contentHash,
            Metadata = metadata,
            UpdatedAt = updatedAt,
            Content = new MemoryStream(bytes)
        };
    }

    public async Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(request.Container);
        var key = ObjectNameValidator.NormalizeObjectKey(request.Key);

        await using var conn = await OpenAsync(ct);

        if (!string.IsNullOrWhiteSpace(request.IfMatch))
        {
            var existing = await ReadInfoInternalAsync(conn, request.Container, key, ct);
            if (existing != null && existing.Etag != request.IfMatch)
                throw new InvalidOperationException("Object delete precondition failed: If-Match etag does not match.");
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vyral_objects WHERE container = $1 AND key = $2";
        cmd.Parameters.AddWithValue(request.Container);
        cmd.Parameters.AddWithValue(key);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default)
    {
        if (request.Limit <= 0 && request.Limit.HasValue)
            throw new InvalidOperationException("Object list limit must be greater than zero.");

        ObjectNameValidator.ValidateContainer(request.Container);
        var pageSize = request.Limit ?? 100;
        var offset = DecodeContinuationToken(request.ContinuationToken);

        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(request.Prefix))
        {
            var prefix = ObjectNameValidator.NormalizeObjectKey(request.Prefix, allowTrailingSlash: true);
            cmd.CommandText = @"
                SELECT key, content_type, content_hash, etag, metadata_json, updated_at,
                       octet_length(content) AS content_length
                FROM vyral_objects
                WHERE container = $1 AND key LIKE $2
                ORDER BY key
                LIMIT $3 OFFSET $4";
            cmd.Parameters.AddWithValue(request.Container);
            cmd.Parameters.AddWithValue(prefix.TrimEnd('/') + "%");
            cmd.Parameters.AddWithValue(pageSize);
            cmd.Parameters.AddWithValue(offset);
        }
        else
        {
            cmd.CommandText = @"
                SELECT key, content_type, content_hash, etag, metadata_json, updated_at,
                       octet_length(content) AS content_length
                FROM vyral_objects
                WHERE container = $1
                ORDER BY key
                LIMIT $2 OFFSET $3";
            cmd.Parameters.AddWithValue(request.Container);
            cmd.Parameters.AddWithValue(pageSize);
            cmd.Parameters.AddWithValue(offset);
        }

        var items = new List<ObjectInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new ObjectInfo
            {
                Container = request.Container,
                Key = reader.GetString(0),
                ContentType = reader.IsDBNull(1) ? null : reader.GetString(1),
                ContentHash = reader.GetString(2),
                Etag = reader.GetString(3),
                Metadata = DeserializeMetadata(reader.IsDBNull(4) ? "{}" : reader.GetString(4)),
                UpdatedAt = reader.GetDateTime(5),
                ContentLength = reader.GetInt64(6)
            });
        }

        string? nextToken = null;
        if (items.Count == pageSize)
        {
            nextToken = EncodeContinuationToken(offset + items.Count);
        }

        return new ObjectListResult { Items = items, ContinuationToken = nextToken };
    }

    private async Task<ObjectInfo?> ReadInfoInternalAsync(NpgsqlConnection conn, string container, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT key, content_type, content_hash, etag, metadata_json, updated_at,
                   octet_length(content) AS content_length
            FROM vyral_objects
            WHERE container = $1 AND key = $2";
        cmd.Parameters.AddWithValue(container);
        cmd.Parameters.AddWithValue(key);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ObjectInfo
        {
            Container = container,
            Key = reader.GetString(0),
            ContentType = reader.IsDBNull(1) ? null : reader.GetString(1),
            ContentHash = reader.GetString(2),
            Etag = reader.GetString(3),
            Metadata = DeserializeMetadata(reader.IsDBNull(4) ? "{}" : reader.GetString(4)),
            UpdatedAt = reader.GetDateTime(5),
            ContentLength = reader.GetInt64(6)
        };
    }

    private static Dictionary<string, string> DeserializeMetadata(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
        catch { return new Dictionary<string, string>(); }
    }

    private static string EncodeContinuationToken(int offset) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static int DecodeContinuationToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token));
            return int.Parse(decoded, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { return 0; }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
