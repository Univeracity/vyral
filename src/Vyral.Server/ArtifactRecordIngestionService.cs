using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Server;

/// <summary>
/// Generic, consumer-neutral ingestion of a record and its raw artifact.
/// Consumers decide record type, collection policy, object naming, schema, and
/// any domain-specific proof bindings. This service only validates the generic
/// storage contract and, when configured, a signed external-context envelope.
/// </summary>
public sealed class ArtifactRecordIngestionService
{
    private readonly IRecordCollectionStore _records;
    private readonly IObjectStore _objects;
    private readonly ArtifactRecordIngestionOptions _options;
    private readonly ExternalContextVerifier _externalContextVerifier;

    public ArtifactRecordIngestionService(
        IRecordCollectionStore records,
        IObjectStore objects,
        ArtifactRecordIngestionOptions options)
    {
        _records = records;
        _objects = objects;
        _options = options;
        _externalContextVerifier = new ExternalContextVerifier(options.ExternalContext);
    }

    public async Task<ArtifactRecordIngestReceipt> IngestAsync(
        ArtifactRecordIngestManifest manifest,
        Stream artifactContent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(artifactContent);
        ValidateManifest(manifest);

        var bytes = await ReadBoundedAsync(artifactContent, _options.MaxArtifactBytes, ct);
        var externalContextVerified = _externalContextVerifier.Verify(manifest.ExternalContext);

        var policy = await _records.GetCollectionPolicyAsync(manifest.Collection, ct);
        if (policy is null)
        {
            throw new InvalidOperationException($"Collection '{manifest.Collection}' does not exist.");
        }

        var contentHash = ComputeSha256(bytes);
        var artifact = await PutArtifactIdempotentlyAsync(manifest.Artifact, bytes, contentHash, ct);
        await _records.UpsertRecordAsync(manifest.Collection, manifest.Record, ct);

        return new ArtifactRecordIngestReceipt
        {
            Accepted = true,
            Collection = manifest.Collection,
            RecordId = manifest.Record.Id,
            PartitionKey = manifest.Record.PartitionKey,
            RecordUri = $"/collections/{Uri.EscapeDataString(manifest.Collection)}/records/{Uri.EscapeDataString(manifest.Record.PartitionKey)}/{Uri.EscapeDataString(manifest.Record.Id)}",
            Artifact = artifact,
            ExternalContextVerified = externalContextVerified,
            ReceivedAt = DateTime.UtcNow
        };
    }

    internal static void ValidateManifest(ArtifactRecordIngestManifest manifest)
    {
        RecordIdentityValidator.ValidateCollectionName(manifest.Collection);
        ArgumentNullException.ThrowIfNull(manifest.Record);
        ArgumentNullException.ThrowIfNull(manifest.Artifact);
        RecordIdentityValidator.ValidateRecord(manifest.Record);
        ObjectNameValidator.ValidateContainer(manifest.Artifact.Container);
        manifest.Artifact.Key = ObjectNameValidator.NormalizeObjectKey(manifest.Artifact.Key);
        ObjectMetadataValidator.ValidateUserMetadata(manifest.Artifact.Metadata);
    }

    private async Task<ObjectInfo> PutArtifactIdempotentlyAsync(
        ArtifactRecordDescriptor descriptor,
        byte[] bytes,
        string contentHash,
        CancellationToken ct)
    {
        try
        {
            await using var content = new MemoryStream(bytes, writable: false);
            return await _objects.PutObjectAsync(new ObjectWriteRequest
            {
                Container = descriptor.Container,
                Key = descriptor.Key,
                Content = content,
                ContentType = descriptor.ContentType,
                Metadata = descriptor.Metadata,
                IfNoneMatch = "*"
            }, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("precondition", StringComparison.OrdinalIgnoreCase))
        {
            var existing = await _objects.GetObjectAsync(new ObjectReadRequest
            {
                Container = descriptor.Container,
                Key = descriptor.Key
            }, ct);
            if (existing is null || !string.Equals(existing.ContentHash, contentHash, StringComparison.Ordinal))
            {
                throw;
            }

            await using (existing.Content)
            {
                return new ObjectInfo
                {
                    Container = existing.Container,
                    Key = existing.Key,
                    ContentType = existing.ContentType,
                    ContentLength = existing.ContentLength,
                    Etag = existing.Etag,
                    ContentHash = existing.ContentHash,
                    Metadata = existing.Metadata,
                    UpdatedAt = existing.UpdatedAt
                };
            }
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream content, long maximumBytes, CancellationToken ct)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await content.ReadAsync(chunk.AsMemory(0, chunk.Length), ct);
            if (read == 0) break;
            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidOperationException($"Artifact cannot exceed {maximumBytes} bytes.");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        if (total == 0)
        {
            throw new InvalidOperationException("Artifact content is required.");
        }
        return buffer.ToArray();
    }

    internal static string ComputeSha256(byte[] bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
}

public sealed class ArtifactRecordIngestionOptions
{
    public long MaxArtifactBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>
    /// Private, non-published object container used between request-bound admission and durable
    /// completion. Hosted API and worker deployments must use the same value.
    /// </summary>
    public string StagingContainer { get; init; } = ArtifactRecordIngestionHostedPlugin.DefaultStagingContainer;
    public ExternalContextVerificationOptions ExternalContext { get; init; } = new();

    public static ArtifactRecordIngestionOptions FromConfiguration(IConfiguration configuration)
    {
        var max = FirstPositiveLong(
            configuration["Ingest:MaxArtifactBytes"],
            configuration["VYRAL_INGEST_MAX_ARTIFACT_BYTES"],
            16 * 1024 * 1024);
        var stagingContainer = FirstNonEmpty(
            configuration["Ingest:StagingContainer"],
            configuration["VYRAL_INGEST_STAGING_CONTAINER"],
            ArtifactRecordIngestionHostedPlugin.DefaultStagingContainer)!;
        ObjectNameValidator.ValidateContainer(stagingContainer);
        return new ArtifactRecordIngestionOptions
        {
            MaxArtifactBytes = max,
            StagingContainer = stagingContainer,
            ExternalContext = ExternalContextVerificationOptions.FromConfiguration(configuration)
        };
    }

    private static long FirstPositiveLong(string? first, string? second, long fallback)
    {
        foreach (var value in new[] { first, second })
        {
            if (long.TryParse(value, out var parsed) && parsed > 0)
            {
                return parsed;
            }
        }
        return fallback;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

/// <summary>
/// Optional generic ES256 JWT verification policy for externally supplied
/// context. It deliberately makes no assumptions about claim names beyond the
/// standard JWT envelope; each consumer verifies its own domain bindings.
/// </summary>
public sealed class ExternalContextVerificationOptions
{
    public string? PublicKeyPem { get; init; }
    public string? KeyId { get; init; }
    public string? Issuer { get; init; }
    public string? Audience { get; init; }
    public bool RequireVerifiedProof { get; init; }
    public int ClockSkewSeconds { get; init; } = 60;

    public static ExternalContextVerificationOptions FromConfiguration(IConfiguration configuration) => new()
    {
        PublicKeyPem = FirstNonEmpty(configuration["ExternalContext:PublicKeyPem"], configuration["VYRAL_EXTERNAL_CONTEXT_PUBLIC_KEY_PEM"]),
        KeyId = FirstNonEmpty(configuration["ExternalContext:KeyId"], configuration["VYRAL_EXTERNAL_CONTEXT_KEY_ID"]),
        Issuer = FirstNonEmpty(configuration["ExternalContext:Issuer"], configuration["VYRAL_EXTERNAL_CONTEXT_ISSUER"]),
        Audience = FirstNonEmpty(configuration["ExternalContext:Audience"], configuration["VYRAL_EXTERNAL_CONTEXT_AUDIENCE"]),
        RequireVerifiedProof = ParseBoolean(FirstNonEmpty(configuration["ExternalContext:RequireVerifiedProof"], configuration["VYRAL_EXTERNAL_CONTEXT_REQUIRE_VERIFIED_PROOF"])),
        ClockSkewSeconds = Math.Max(0, ParseInt(FirstNonEmpty(configuration["ExternalContext:ClockSkewSeconds"], configuration["VYRAL_EXTERNAL_CONTEXT_CLOCK_SKEW_SECONDS"]), 60))
    };

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    private static bool ParseBoolean(string? value) => bool.TryParse(value, out var parsed) && parsed;
    private static int ParseInt(string? value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;
}

internal sealed class ExternalContextVerifier
{
    private readonly ExternalContextVerificationOptions _options;

    public ExternalContextVerifier(ExternalContextVerificationOptions options)
    {
        _options = options;
        if (_options.RequireVerifiedProof && !IsConfigured)
        {
            throw new InvalidOperationException("External-context verification requires publicKeyPem, keyId, issuer, and audience.");
        }
    }

    private bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.PublicKeyPem) &&
        !string.IsNullOrWhiteSpace(_options.KeyId) &&
        !string.IsNullOrWhiteSpace(_options.Issuer) &&
        !string.IsNullOrWhiteSpace(_options.Audience);

    public bool Verify(ExternalContextProof? proof)
    {
        if (proof is null || string.IsNullOrWhiteSpace(proof.Token))
        {
            if (_options.RequireVerifiedProof)
            {
                throw new InvalidOperationException("A verified external-context proof is required.");
            }
            return false;
        }
        if (!IsConfigured)
        {
            throw new InvalidOperationException("An external-context proof was supplied, but verification is not configured.");
        }

        var parts = proof.Token.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("External-context JWT must contain three compact parts.");
        }

        using var headerDocument = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        using var claimsDocument = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var header = headerDocument.RootElement;
        var claims = claimsDocument.RootElement;
        if (header.ValueKind != JsonValueKind.Object || claims.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("External-context JWT header and claims must be objects.");
        }
        if (!string.Equals(GetString(header, "alg"), "ES256", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("External-context JWT alg must be ES256.");
        }
        if (!string.Equals(GetString(header, "kid"), _options.KeyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("External-context JWT key id is not trusted.");
        }

        var signature = Base64UrlDecode(parts[2]);
        if (signature.Length != 64)
        {
            throw new InvalidOperationException("External-context ES256 signature must be 64 bytes.");
        }
        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(_options.PublicKeyPem!.Replace("\\n", "\n", StringComparison.Ordinal).AsSpan());
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        if (!publicKey.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new InvalidOperationException("External-context JWT signature is invalid.");
        }
        if (!string.Equals(GetString(claims, "iss"), _options.Issuer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("External-context JWT issuer is not trusted.");
        }
        if (!HasAudience(claims, _options.Audience!))
        {
            throw new InvalidOperationException("External-context JWT audience is not trusted.");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var skew = _options.ClockSkewSeconds;
        var exp = GetUnixSeconds(claims, "exp") ?? throw new InvalidOperationException("External-context JWT exp claim is required.");
        if (now > exp + skew)
        {
            throw new InvalidOperationException("External-context JWT is expired.");
        }
        var issuedAt = GetUnixSeconds(claims, "iat");
        if (issuedAt.HasValue && issuedAt.Value > now + skew)
        {
            throw new InvalidOperationException("External-context JWT issued-at is in the future.");
        }
        return true;
    }

    private static string? GetString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static bool HasAudience(JsonElement claims, string expected)
    {
        if (!claims.TryGetProperty("aud", out var audience)) return false;
        return audience.ValueKind switch
        {
            JsonValueKind.String => string.Equals(audience.GetString(), expected, StringComparison.Ordinal),
            JsonValueKind.Array => audience.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), expected, StringComparison.Ordinal)),
            _ => false
        };
    }

    private static long? GetUnixSeconds(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item)) return null;
        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var numeric)) return numeric;
        return item.ValueKind == JsonValueKind.String && long.TryParse(item.GetString(), out var text) ? text : null;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }
}
