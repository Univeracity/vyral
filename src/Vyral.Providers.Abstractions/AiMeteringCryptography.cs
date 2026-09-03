using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

/// <summary>
/// Canonicalization, hashing, ES256 signing, and verification for AI metering receipts and reviews.
/// Keys remain caller-owned so signing can occur at the actual observation boundary.
/// </summary>
public static class AiMeteringCryptography
{
    public const string Es256 = "ES256";
    public const string IntegrityV1 = "vyral.ai-metering-integrity.v1";
    public const int MaxEvidenceJsonBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    /// <summary>Strictly parses a bounded receipt, rejecting duplicate and unknown JSON members.</summary>
    public static AiMeteringReceipt DeserializeReceipt(ReadOnlySpan<byte> utf8Json)
    {
        ValidateJsonEnvelope(utf8Json);
        var receipt = JsonSerializer.Deserialize<AiMeteringReceipt>(utf8Json, StrictJsonOptions)
            ?? throw new JsonException("AI metering receipt cannot be null.");
        AiMeteringValidator.ValidateReceipt(receipt).ThrowIfInvalid();
        return receipt;
    }

    /// <summary>Strictly parses a bounded review, rejecting duplicate and unknown JSON members.</summary>
    public static AiMeteringReview DeserializeReview(ReadOnlySpan<byte> utf8Json)
    {
        ValidateJsonEnvelope(utf8Json);
        var review = JsonSerializer.Deserialize<AiMeteringReview>(utf8Json, StrictJsonOptions)
            ?? throw new JsonException("AI metering review cannot be null.");
        AiMeteringValidator.ValidateReview(review).ThrowIfInvalid();
        return review;
    }

    public static void SignReceipt(AiMeteringReceipt receipt, ECDsa privateKey, string issuer, string keyId)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(privateKey);
        RequireIdentity(issuer, nameof(issuer));
        RequireIdentity(keyId, nameof(keyId));

        RequireP256(privateKey, nameof(privateKey));
        var request = CreateReceiptSigningRequest(receipt, issuer, keyId);
        var signature = privateKey.SignData(
            request.Data.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        ApplyReceiptSignature(receipt, request, signature);
    }

    public static AiMeteringVerificationResult VerifyReceipt(
        AiMeteringReceipt receipt,
        ECDsa publicKey,
        string? expectedIssuer = null,
        string? expectedKeyId = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(publicKey);
        var validation = AiMeteringValidator.ValidateReceipt(receipt, requireIntegrity: true);
        return Verify(CanonicalReceiptPayload(receipt), receipt.Schema, receipt.Integrity, publicKey, validation.Errors, expectedIssuer, expectedKeyId);
    }

    public static void SignReview(AiMeteringReview review, ECDsa privateKey, string issuer, string keyId)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(privateKey);
        RequireIdentity(issuer, nameof(issuer));
        RequireIdentity(keyId, nameof(keyId));

        RequireP256(privateKey, nameof(privateKey));
        var request = CreateReviewSigningRequest(review, issuer, keyId);
        var signature = privateKey.SignData(
            request.Data.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        ApplyReviewSignature(review, request, signature);
    }

    public static AiMeteringVerificationResult VerifyReview(
        AiMeteringReview review,
        ECDsa publicKey,
        string? expectedIssuer = null,
        string? expectedKeyId = null)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(publicKey);
        var validation = AiMeteringValidator.ValidateReview(review, requireIntegrity: true);
        return Verify(CanonicalReviewPayload(review), review.Schema, review.Integrity, publicKey, validation.Errors, expectedIssuer, expectedKeyId);
    }

    public static byte[] CanonicalReceiptPayload(AiMeteringReceipt receipt) => CanonicalPayload(receipt);

    public static byte[] CanonicalReviewPayload(AiMeteringReview review) => CanonicalPayload(review);

    public static string ComputeReceiptPayloadHash(AiMeteringReceipt receipt) => Sha256(CanonicalReceiptPayload(receipt));

    public static string ComputeReviewPayloadHash(AiMeteringReview review) => Sha256(CanonicalReviewPayload(review));

    /// <summary>Hashes the complete signed or unsigned receipt envelope, including integrity metadata.</summary>
    public static string ComputeReceiptEnvelopeHash(AiMeteringReceipt receipt) => Sha256(CanonicalEnvelope(receipt));

    public static string ComputeReviewEnvelopeHash(AiMeteringReview review) => Sha256(CanonicalEnvelope(review));

    /// <summary>Creates the protected bytes a remote ES256 signer must sign for a receipt.</summary>
    public static AiMeteringSigningRequest CreateReceiptSigningRequest(
        AiMeteringReceipt receipt,
        string issuer,
        string keyId)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var validation = AiMeteringValidator.ValidateReceipt(receipt, requireIntegrity: false, allowPendingIntegrity: true);
        validation.ThrowIfInvalid();
        return CreateSigningRequest(CanonicalReceiptPayload(receipt), receipt.Schema, issuer, keyId);
    }

    /// <summary>Creates the protected bytes a remote ES256 signer must sign for a review.</summary>
    public static AiMeteringSigningRequest CreateReviewSigningRequest(
        AiMeteringReview review,
        string issuer,
        string keyId)
    {
        ArgumentNullException.ThrowIfNull(review);
        var validation = AiMeteringValidator.ValidateReview(review, requireIntegrity: false);
        validation.ThrowIfInvalid();
        return CreateSigningRequest(CanonicalReviewPayload(review), review.Schema, issuer, keyId);
    }

    /// <summary>Applies a raw 64-byte IEEE P1363 signature returned by a remote signer.</summary>
    public static void ApplyReceiptSignature(
        AiMeteringReceipt receipt,
        AiMeteringSigningRequest request,
        ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ApplySignature(
            receipt.Schema,
            CanonicalReceiptPayload(receipt),
            request,
            signature,
            integrity => receipt.Integrity = integrity);
    }

    /// <summary>Applies a raw 64-byte IEEE P1363 signature returned by a remote signer.</summary>
    public static void ApplyReviewSignature(
        AiMeteringReview review,
        AiMeteringSigningRequest request,
        ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(review);
        ApplySignature(
            review.Schema,
            CanonicalReviewPayload(review),
            request,
            signature,
            integrity => review.Integrity = integrity);
    }

    private static AiMeteringSigningRequest CreateSigningRequest(
        byte[] payload,
        string evidenceSchema,
        string issuer,
        string keyId)
    {
        RequireIdentity(issuer, nameof(issuer));
        RequireIdentity(keyId, nameof(keyId));
        var payloadHash = Sha256(payload);
        return new AiMeteringSigningRequest(
            evidenceSchema,
            issuer,
            keyId,
            payloadHash,
            BuildProtectedSigningInput(evidenceSchema, Es256, issuer, keyId, payloadHash));
    }

    private static void ApplySignature(
        string evidenceSchema,
        byte[] payload,
        AiMeteringSigningRequest request,
        ReadOnlySpan<byte> signature,
        Action<AiMeteringIntegrity> apply)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.EvidenceSchema, evidenceSchema, StringComparison.Ordinal) ||
            !FixedTimeHashEquals(Sha256(payload), request.PayloadHash))
        {
            throw new InvalidOperationException("The signing request does not match the current metering evidence payload.");
        }
        if (signature.Length != 64)
        {
            throw new ArgumentException("ES256 requires a 64-byte IEEE P1363 signature.", nameof(signature));
        }
        apply(new AiMeteringIntegrity
        {
            Algorithm = Es256,
            Issuer = request.Issuer,
            KeyId = request.KeyId,
            PayloadHash = request.PayloadHash,
            Signature = Base64UrlEncode(signature.ToArray())
        });
    }

    private static AiMeteringVerificationResult Verify(
        byte[] payload,
        string evidenceSchema,
        AiMeteringIntegrity? integrity,
        ECDsa publicKey,
        IEnumerable<string> validationErrors,
        string? expectedIssuer,
        string? expectedKeyId)
    {
        var errors = validationErrors.ToList();
        var payloadHash = Sha256(payload);
        var trustedCurve = IsP256(publicKey);
        if (!trustedCurve)
        {
            errors.Add("verification key must use the NIST P-256 curve");
        }
        if (integrity is null)
        {
            errors.Add("integrity is required");
            return new AiMeteringVerificationResult(payloadHash, errors);
        }

        if (!string.Equals(integrity.Algorithm, Es256, StringComparison.Ordinal))
        {
            errors.Add($"integrity algorithm must be {Es256}");
        }
        if (expectedIssuer is not null && !string.Equals(integrity.Issuer, expectedIssuer, StringComparison.Ordinal))
        {
            errors.Add("integrity issuer does not match the trusted issuer");
        }
        if (expectedKeyId is not null && !string.Equals(integrity.KeyId, expectedKeyId, StringComparison.Ordinal))
        {
            errors.Add("integrity keyId does not match the trusted key");
        }
        if (!FixedTimeHashEquals(payloadHash, integrity.PayloadHash))
        {
            errors.Add("integrity payloadHash does not match the canonical payload");
        }

        byte[] signature;
        try
        {
            signature = Base64UrlDecode(integrity.Signature);
        }
        catch (FormatException)
        {
            errors.Add("integrity signature is not valid base64url");
            return new AiMeteringVerificationResult(payloadHash, errors);
        }

        if (signature.Length != 64)
        {
            errors.Add("integrity signature must be a 64-byte IEEE P1363 ES256 signature");
        }
        else if (trustedCurve && !publicKey.VerifyData(
            BuildProtectedSigningInput(evidenceSchema, integrity.Algorithm, integrity.Issuer, integrity.KeyId, integrity.PayloadHash),
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            errors.Add("integrity signature is invalid");
        }

        return new AiMeteringVerificationResult(payloadHash, errors);
    }

    private static byte[] BuildProtectedSigningInput(
        string? evidenceSchema,
        string? algorithm,
        string? issuer,
        string? keyId,
        string? payloadHash)
    {
        var statement = new JsonObject
        {
            ["schema"] = IntegrityV1,
            ["algorithm"] = algorithm,
            ["evidenceSchema"] = evidenceSchema,
            ["issuer"] = issuer,
            ["keyId"] = keyId,
            ["payloadHash"] = payloadHash
        };
        return Canonicalize(statement);
    }

    private static byte[] CanonicalPayload<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, JsonOptions)?.AsObject()
            ?? throw new InvalidOperationException("Metering evidence must serialize as a JSON object.");
        node.Remove("integrity");
        return Canonicalize(node);
    }

    private static void ValidateJsonEnvelope(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > MaxEvidenceJsonBytes)
        {
            throw new JsonException($"AI metering JSON must contain between 1 and {MaxEvidenceJsonBytes} bytes.");
        }

        var propertySets = new Stack<HashSet<string>>();
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                propertySets.Push(new HashSet<string>(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (propertySets.Count == 0 || !propertySets.Peek().Add(reader.GetString() ?? string.Empty))
                {
                    throw new JsonException("AI metering JSON contains a duplicate object member.");
                }
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                propertySets.Pop();
            }
        }
    }

    private static byte[] CanonicalEnvelope<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, JsonOptions)
            ?? throw new InvalidOperationException("Metering evidence must serialize as JSON.");
        return Canonicalize(node);
    }

    private static byte[] Canonicalize(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            WriteCanonical(node, writer);
        }
        return stream.ToArray();
    }

    private static void WriteCanonical(JsonNode? node, Utf8JsonWriter writer)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteCanonical(item, writer);
                }
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer, JsonOptions);
                break;
        }
    }

    private static string Sha256(byte[] value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static bool FixedTimeHashEquals(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string? value)
    {
        if (value is null)
        {
            throw new FormatException("A base64url value is required.");
        }
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url length.")
        };
        return Convert.FromBase64String(normalized);
    }

    private static void RequireIdentity(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty identity is required.", parameterName);
        }
        if (value.Length > 512)
        {
            throw new ArgumentException("An identity cannot exceed 512 characters.", parameterName);
        }
    }

    private static void RequireP256(ECDsa key, string parameterName)
    {
        if (!IsP256(key))
        {
            throw new ArgumentException("ES256 requires a NIST P-256 key.", parameterName);
        }
    }

    public static bool IsP256(ECDsa key)
    {
        try
        {
            var curve = key.ExportParameters(includePrivateParameters: false).Curve;
            return string.Equals(curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

public sealed class AiMeteringVerificationResult
{
    internal AiMeteringVerificationResult(string payloadHash, IEnumerable<string> errors)
    {
        PayloadHash = payloadHash;
        Errors = errors.Distinct(StringComparer.Ordinal).ToList();
    }

    public bool Valid => Errors.Count == 0;
    public string PayloadHash { get; }
    public IReadOnlyList<string> Errors { get; }

    public void ThrowIfInvalid()
    {
        if (!Valid)
        {
            throw new InvalidOperationException("AI metering evidence is invalid: " + string.Join("; ", Errors));
        }
    }
}

/// <summary>
/// Immutable protected signing input for an external KMS or HSM. The bytes bind the evidence
/// schema, algorithm, issuer, key ID, and canonical payload hash without exposing raw content.
/// </summary>
public sealed class AiMeteringSigningRequest
{
    private readonly byte[] _data;

    internal AiMeteringSigningRequest(
        string evidenceSchema,
        string issuer,
        string keyId,
        string payloadHash,
        byte[] data)
    {
        EvidenceSchema = evidenceSchema;
        Issuer = issuer;
        KeyId = keyId;
        PayloadHash = payloadHash;
        _data = data.ToArray();
    }

    public string Algorithm => AiMeteringCryptography.Es256;
    public string EvidenceSchema { get; }
    public string Issuer { get; }
    public string KeyId { get; }
    public string PayloadHash { get; }
    public ReadOnlyMemory<byte> Data => _data.ToArray();
}
