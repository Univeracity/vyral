using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

public static class RecordSearchProjectionGenerationSchemas
{
    public const string DescriptorV1 = "vyral.record-search-projection-generation.v1";
    public const string SearchRequestV1 = "vyral.record-search-projection-request.v1";
    public const string SearchResultV1 = "vyral.record-search-projection-result.v1";
    public const string InspectionV1 = "vyral.record-search-projection-inspection.v1";
    public const string BuildRequestV1 = "vyral.record-search-projection-build-request.v1";
    public const string BuildReceiptV1 = "vyral.record-search-projection-build-receipt.v1";
}

public static class RecordSearchProjectionBuildStatuses
{
    public const string Verified = "verified";
}

public static class RecordSearchProjectionGenerationStates
{
    public const string Active = "active";
    public const string Retained = "retained";
    public const string Retired = "retired";
}

public static class RecordSearchProjectionCoverageStatuses
{
    public const string Complete = "complete";
    public const string Incomplete = "incomplete";
    public const string Unavailable = "unavailable";
}

public static class RecordSearchProjectionResultStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public static class RecordSearchProjectionFailureCodes
{
    public const string GenerationUnavailable = "generationUnavailable";
    public const string GenerationRetired = "generationRetired";
    public const string GenerationDescriptorMismatch = "generationDescriptorMismatch";
    public const string CoverageIncomplete = "coverageIncomplete";
    public const string InvalidContinuation = "invalidContinuation";
    public const string ExpiredContinuation = "expiredContinuation";
    public const string DeadlineExceeded = "deadlineExceeded";
    public const string WorkLimitExceeded = "workLimitExceeded";
    public const string UnsupportedQuery = "unsupportedQuery";
}

public static class RecordSearchProjectionCacheStatuses
{
    public const string Hit = "hit";
    public const string Miss = "miss";
    public const string Bypass = "bypass";
    public const string NotApplicable = "notApplicable";
}

public static class RecordSearchProjectionGenerationCapabilities
{
    public const string CompleteCoverage = "completeCoverage";
    /// <summary>
    /// Safety property: every continuation the adapter emits is authenticated and bound to its
    /// immutable generation. This does not itself claim that a particular query shape is pageable;
    /// a bounded first-page-only implementation can satisfy it by never emitting a continuation.
    /// </summary>
    public const string GenerationPinnedContinuation = "generationPinnedContinuation";
    public const string Lexical = "lexical";
    public const string Vector = "vector";
}

/// <summary>
/// Portable identity and completeness evidence for one immutable derived search generation.
/// Provider-native index files and routing structures remain opaque; this descriptor binds only
/// the evidence a consumer needs to select and verify a generation safely.
/// </summary>
public sealed class RecordSearchProjectionGenerationDescriptor
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = RecordSearchProjectionGenerationSchemas.DescriptorV1;

    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("generationId")]
    public string GenerationId { get; set; } = string.Empty;

    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = string.Empty;

    [JsonPropertyName("strategyVersion")]
    public string StrategyVersion { get; set; } = string.Empty;

    [JsonPropertyName("sourceManifestDigest")]
    public string SourceManifestDigest { get; set; } = string.Empty;

    [JsonPropertyName("recordRevisionSetDigest")]
    public string RecordRevisionSetDigest { get; set; } = string.Empty;

    [JsonPropertyName("projectionSchemaDigest")]
    public string ProjectionSchemaDigest { get; set; } = string.Empty;

    [JsonPropertyName("analyzerDigest")]
    public string? AnalyzerDigest { get; set; }

    [JsonPropertyName("configurationDigest")]
    public string ConfigurationDigest { get; set; } = string.Empty;

    [JsonPropertyName("expectedItemCount")]
    public long ExpectedItemCount { get; set; }

    [JsonPropertyName("expectedPartitions")]
    public List<string> ExpectedPartitions { get; set; } = new();

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = new();

    [JsonPropertyName("artifacts")]
    public List<RecordSearchProjectionGenerationArtifact> Artifacts { get; set; } = new();

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    [JsonPropertyName("descriptorDigest")]
    public string DescriptorDigest { get; set; } = string.Empty;
}

public sealed class RecordSearchProjectionGenerationArtifact
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }
}

public sealed class GenerationBoundRecordSearchProjectionRequest
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = RecordSearchProjectionGenerationSchemas.SearchRequestV1;

    /// <summary>
    /// Exact generation requested. Null selects the active generation for a first page. A
    /// continuation always selects its retained generation and cannot be remapped to the active one.
    /// </summary>
    [JsonPropertyName("generationId")]
    public string? GenerationId { get; set; }

    /// <summary>Optional caller fence against descriptor substitution.</summary>
    [JsonPropertyName("expectedDescriptorDigest")]
    public string? ExpectedDescriptorDigest { get; set; }

    [JsonPropertyName("query")]
    public QueryEnvelope Query { get; set; } = new();

    [JsonPropertyName("deadlineUtc")]
    public DateTime? DeadlineUtc { get; set; }
}

public sealed class RecordSearchProjectionCoverage
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = RecordSearchProjectionCoverageStatuses.Unavailable;

    [JsonPropertyName("requestedPartitions")]
    public List<string> RequestedPartitions { get; set; } = new();

    [JsonPropertyName("coveredPartitions")]
    public List<string> CoveredPartitions { get; set; } = new();

    [JsonPropertyName("missingPartitions")]
    public List<string> MissingPartitions { get; set; } = new();
}

public sealed class RecordSearchProjectionWorkDiagnostics
{
    [JsonPropertyName("workLimit")]
    public long? WorkLimit { get; set; }

    [JsonPropertyName("workUnits")]
    public long? WorkUnits { get; set; }

    [JsonPropertyName("candidateBound")]
    public long? CandidateBound { get; set; }

    [JsonPropertyName("candidateCount")]
    public long? CandidateCount { get; set; }

    [JsonPropertyName("returnedCount")]
    public long ReturnedCount { get; set; }

    [JsonPropertyName("cacheStatus")]
    public string CacheStatus { get; set; } = RecordSearchProjectionCacheStatuses.NotApplicable;

    /// <summary>
    /// Bounded, privacy-safe adapter diagnostics. Provider SDK objects, query text, credentials,
    /// authorization decisions, and raw logs do not belong here.
    /// </summary>
    [JsonPropertyName("details")]
    public JsonObject? Details { get; set; }
}

public sealed class RecordSearchProjectionFailure
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("retryable")]
    public bool Retryable { get; set; }
}

/// <summary>
/// Candidate-only result bound to one immutable generation. A failed or incomplete result must
/// never contain candidates or a continuation token.
/// </summary>
public sealed class GenerationBoundRecordSearchProjectionResult
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = RecordSearchProjectionGenerationSchemas.SearchResultV1;

    [JsonPropertyName("status")]
    public string Status { get; set; } = RecordSearchProjectionResultStatuses.Failed;

    [JsonPropertyName("generationId")]
    public string? GenerationId { get; set; }

    [JsonPropertyName("generationDescriptorDigest")]
    public string? GenerationDescriptorDigest { get; set; }

    [JsonPropertyName("items")]
    public List<RecordSearchProjectionCandidate> Items { get; set; } = new();

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }

    [JsonPropertyName("consistency")]
    public string Consistency { get; set; } = "immutableGeneration";

    [JsonPropertyName("coverage")]
    public RecordSearchProjectionCoverage Coverage { get; set; } = new();

    [JsonPropertyName("diagnostics")]
    public RecordSearchProjectionWorkDiagnostics Diagnostics { get; set; } = new();

    [JsonPropertyName("failure")]
    public RecordSearchProjectionFailure? Failure { get; set; }
}

public sealed class RecordSearchProjectionGenerationInspection
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = RecordSearchProjectionGenerationSchemas.InspectionV1;

    [JsonPropertyName("descriptor")]
    public RecordSearchProjectionGenerationDescriptor Descriptor { get; set; } = new();

    [JsonPropertyName("state")]
    public string State { get; set; } = RecordSearchProjectionGenerationStates.Retained;

    [JsonPropertyName("availablePartitions")]
    public List<string> AvailablePartitions { get; set; } = new();

    [JsonPropertyName("coverageStatus")]
    public string CoverageStatus { get; set; } = RecordSearchProjectionCoverageStatuses.Unavailable;

    [JsonPropertyName("observedAtUtc")]
    public DateTime ObservedAtUtc { get; set; }
}

/// <summary>
/// Portable payload for durable construction and verification. Idempotency belongs to the
/// execution admission request so it is hashed and enforced by <c>IExecutionRuntime</c> rather than
/// duplicated inside this payload.
/// </summary>
public sealed class RecordSearchProjectionGenerationBuildRequest
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = RecordSearchProjectionGenerationSchemas.BuildRequestV1;

    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("generationId")]
    public string GenerationId { get; set; } = string.Empty;

    [JsonPropertyName("builderId")]
    public string BuilderId { get; set; } = string.Empty;

    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = string.Empty;

    [JsonPropertyName("strategyVersion")]
    public string StrategyVersion { get; set; } = string.Empty;

    [JsonPropertyName("sourceManifestRef")]
    public string SourceManifestRef { get; set; } = string.Empty;

    [JsonPropertyName("sourceManifestDigest")]
    public string SourceManifestDigest { get; set; } = string.Empty;

    [JsonPropertyName("expectedRecordRevisionSetDigest")]
    public string? ExpectedRecordRevisionSetDigest { get; set; }

    [JsonPropertyName("projectionSchemaDigest")]
    public string ProjectionSchemaDigest { get; set; } = string.Empty;

    [JsonPropertyName("analyzerDigest")]
    public string? AnalyzerDigest { get; set; }

    [JsonPropertyName("configurationDigest")]
    public string ConfigurationDigest { get; set; } = string.Empty;

    [JsonPropertyName("expectedItemCount")]
    public long ExpectedItemCount { get; set; }

    [JsonPropertyName("expectedPartitions")]
    public List<string> ExpectedPartitions { get; set; } = new();

    [JsonPropertyName("deadlineUtc")]
    public DateTime? DeadlineUtc { get; set; }
}

public sealed class RecordSearchProjectionGenerationBuildProgress
{
    [JsonPropertyName("stage")]
    public string Stage { get; set; } = string.Empty;

    [JsonPropertyName("completed")]
    public long Completed { get; set; }

    [JsonPropertyName("total")]
    public long? Total { get; set; }

    [JsonPropertyName("checkpoint")]
    public JsonObject? Checkpoint { get; set; }
}

/// <summary>
/// Compact terminal receipt. Full provider logs and index bytes remain in immutable artifacts;
/// this receipt binds their digests to the verified portable descriptor.
/// </summary>
public sealed class RecordSearchProjectionGenerationBuildReceipt
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = RecordSearchProjectionGenerationSchemas.BuildReceiptV1;

    [JsonPropertyName("status")]
    public string Status { get; set; } = RecordSearchProjectionBuildStatuses.Verified;

    [JsonPropertyName("builderId")]
    public string BuilderId { get; set; } = string.Empty;

    [JsonPropertyName("descriptor")]
    public RecordSearchProjectionGenerationDescriptor Descriptor { get; set; } = new();

    [JsonPropertyName("evaluationReceiptDigest")]
    public string? EvaluationReceiptDigest { get; set; }

    [JsonPropertyName("builtAtUtc")]
    public DateTime BuiltAtUtc { get; set; }
}

public sealed class HydratedGenerationBoundRecordSearchProjectionResult
{
    [JsonPropertyName("projection")]
    public GenerationBoundRecordSearchProjectionResult Projection { get; set; } = new();

    [JsonPropertyName("items")]
    public List<VyralRecordMatch> Items { get; set; } = new();

    [JsonPropertyName("staleCandidatesDiscarded")]
    public int StaleCandidatesDiscarded { get; set; }
}

/// <summary>Deterministic validation and hashing shared by projection implementations.</summary>
public static class RecordSearchProjectionGenerationContract
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string SealDescriptor(RecordSearchProjectionGenerationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateDescriptor(descriptor, requireDescriptorDigest: false);
        descriptor.DescriptorDigest = ComputeDescriptorDigest(descriptor);
        return descriptor.DescriptorDigest;
    }

    public static string ComputeDescriptorDigest(RecordSearchProjectionGenerationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var material = new
        {
            schema = descriptor.Schema,
            collection = descriptor.Collection,
            generationId = descriptor.GenerationId,
            providerId = descriptor.ProviderId,
            profileId = descriptor.ProfileId,
            strategyVersion = descriptor.StrategyVersion,
            sourceManifestDigest = descriptor.SourceManifestDigest,
            recordRevisionSetDigest = descriptor.RecordRevisionSetDigest,
            projectionSchemaDigest = descriptor.ProjectionSchemaDigest,
            analyzerDigest = descriptor.AnalyzerDigest,
            configurationDigest = descriptor.ConfigurationDigest,
            expectedItemCount = descriptor.ExpectedItemCount,
            expectedPartitions = descriptor.ExpectedPartitions.OrderBy(value => value, StringComparer.Ordinal),
            capabilities = descriptor.Capabilities.OrderBy(value => value, StringComparer.Ordinal),
            artifacts = descriptor.Artifacts
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .Select(value => new
                {
                    id = value.Id,
                    kind = value.Kind,
                    contentHash = value.ContentHash,
                    sizeBytes = value.SizeBytes,
                    mediaType = value.MediaType
                }),
            createdAtUtc = descriptor.CreatedAtUtc.ToUniversalTime()
        };
        return Hash(CanonicalJson.SerializeUtf8(material, JsonOptions));
    }

    public static string ComputeRequestFingerprint(
        GenerationBoundRecordSearchProjectionRequest request,
        string generationId,
        string descriptorDigest,
        IReadOnlyCollection<string> requestedPartitions)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Query ?? throw new InvalidOperationException("A generation-bound projection request requires a query.");
        var material = new
        {
            schema = request.Schema,
            generationId,
            descriptorDigest,
            requestedPartitions = requestedPartitions.OrderBy(value => value, StringComparer.Ordinal),
            query = new
            {
                partitionKeys = query.PartitionKeys?.OrderBy(value => value, StringComparer.Ordinal),
                filter = query.Filter,
                vector = query.Vector,
                lexical = query.Lexical,
                orderBy = query.OrderBy,
                limit = query.Limit
            }
        };
        return Hash(CanonicalJson.SerializeUtf8(material, JsonOptions));
    }

    public static void ValidateDescriptor(
        RecordSearchProjectionGenerationDescriptor descriptor,
        bool requireDescriptorDigest = true)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        RequireExact(descriptor.Schema, RecordSearchProjectionGenerationSchemas.DescriptorV1, "descriptor schema");
        RequireIdentifier(descriptor.Collection, "collection");
        RequireIdentifier(descriptor.GenerationId, "generationId");
        RequireIdentifier(descriptor.ProviderId, "providerId");
        RequireIdentifier(descriptor.ProfileId, "profileId");
        RequireIdentifier(descriptor.StrategyVersion, "strategyVersion");
        RequireSha256(descriptor.SourceManifestDigest, "sourceManifestDigest");
        RequireSha256(descriptor.RecordRevisionSetDigest, "recordRevisionSetDigest");
        RequireSha256(descriptor.ProjectionSchemaDigest, "projectionSchemaDigest");
        if (descriptor.AnalyzerDigest is not null)
        {
            RequireSha256(descriptor.AnalyzerDigest, "analyzerDigest");
        }
        RequireSha256(descriptor.ConfigurationDigest, "configurationDigest");

        if (descriptor.ExpectedItemCount < 0)
        {
            throw new InvalidOperationException("expectedItemCount cannot be negative.");
        }
        RequireCanonicalSet(descriptor.ExpectedPartitions, "expectedPartitions", requireNonEmpty: true);
        RequireCanonicalSet(descriptor.Capabilities, "capabilities", requireNonEmpty: true);
        if (!descriptor.Capabilities.Contains(RecordSearchProjectionGenerationCapabilities.CompleteCoverage, StringComparer.Ordinal) ||
            !descriptor.Capabilities.Contains(RecordSearchProjectionGenerationCapabilities.GenerationPinnedContinuation, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "A generation-bound descriptor requires complete-coverage and generation-pinned-continuation capabilities.");
        }
        if (descriptor.CreatedAtUtc == default || descriptor.CreatedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException("createdAtUtc must be a non-default UTC timestamp.");
        }

        var artifactIds = new List<string>(descriptor.Artifacts.Count);
        foreach (var artifact in descriptor.Artifacts)
        {
            RequireIdentifier(artifact.Id, "artifact id");
            RequireIdentifier(artifact.Kind, "artifact kind");
            RequireSha256(artifact.ContentHash, $"artifact '{artifact.Id}' contentHash");
            if (artifact.SizeBytes < 0)
            {
                throw new InvalidOperationException($"Artifact '{artifact.Id}' sizeBytes cannot be negative.");
            }
            if (artifact.MediaType is { } mediaType &&
                (string.IsNullOrWhiteSpace(mediaType) || mediaType.Length > 200 ||
                 !string.Equals(mediaType, mediaType.Trim(), StringComparison.Ordinal) ||
                 mediaType.Any(char.IsControl)))
            {
                throw new InvalidOperationException($"Artifact '{artifact.Id}' mediaType is invalid.");
            }
            artifactIds.Add(artifact.Id);
        }
        RequireCanonicalSet(artifactIds, "artifact ids", requireNonEmpty: false);

        if (!requireDescriptorDigest)
        {
            return;
        }
        RequireSha256(descriptor.DescriptorDigest, "descriptorDigest");
        var actual = ComputeDescriptorDigest(descriptor);
        if (!string.Equals(actual, descriptor.DescriptorDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("descriptorDigest does not match the canonical descriptor material.");
        }
    }

    public static void ValidateRequest(GenerationBoundRecordSearchProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireExact(request.Schema, RecordSearchProjectionGenerationSchemas.SearchRequestV1, "request schema");
        if (request.GenerationId is not null)
        {
            RequireIdentifier(request.GenerationId, "generationId");
        }
        if (request.ExpectedDescriptorDigest is not null)
        {
            RequireSha256(request.ExpectedDescriptorDigest, "expectedDescriptorDigest");
        }
        if (request.Query is null)
        {
            throw new InvalidOperationException("A generation-bound projection request requires a query.");
        }
        if (request.DeadlineUtc is { } deadline && deadline.Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException("deadlineUtc must be UTC when supplied.");
        }
    }

    public static void ValidateResult(GenerationBoundRecordSearchProjectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        RequireExact(result.Schema, RecordSearchProjectionGenerationSchemas.SearchResultV1, "result schema");
        if (result.Consistency != "immutableGeneration")
        {
            throw new InvalidOperationException("A generation-bound result must use immutableGeneration consistency.");
        }
        ValidateCoverage(result.Coverage);
        ValidateDiagnostics(result.Diagnostics);
        if (result.GenerationId is not null)
        {
            RequireIdentifier(result.GenerationId, "generationId");
        }
        if (result.GenerationDescriptorDigest is not null)
        {
            RequireSha256(result.GenerationDescriptorDigest, "generationDescriptorDigest");
            if (result.GenerationId is null)
            {
                throw new InvalidOperationException("A generation descriptor digest requires its generation ID.");
            }
        }
        if (result.ContinuationToken is { Length: > 8192 })
        {
            throw new InvalidOperationException("A generation-bound continuation cannot exceed 8192 characters.");
        }

        if (result.Status == RecordSearchProjectionResultStatuses.Succeeded)
        {
            RequireIdentifier(result.GenerationId, "generationId");
            RequireSha256(result.GenerationDescriptorDigest, "generationDescriptorDigest");
            if (result.Failure is not null)
            {
                throw new InvalidOperationException("A successful generation-bound result cannot include a failure.");
            }
            if (result.Coverage.Status != RecordSearchProjectionCoverageStatuses.Complete)
            {
                throw new InvalidOperationException("A successful generation-bound result requires complete coverage.");
            }
            if (result.Diagnostics.ReturnedCount != result.Items.Count)
            {
                throw new InvalidOperationException("returnedCount must equal the number of returned candidates.");
            }
            ValidateCandidates(result.Items);
            return;
        }

        if (result.Status != RecordSearchProjectionResultStatuses.Failed)
        {
            throw new InvalidOperationException("A generation-bound result status must be succeeded or failed.");
        }
        if (result.Failure is null || string.IsNullOrWhiteSpace(result.Failure.Message))
        {
            throw new InvalidOperationException("A failed generation-bound result requires a structured failure.");
        }
        RequireIdentifier(result.Failure.Code, "failure code");
        if (result.Failure.Message.Length > 1000)
        {
            throw new InvalidOperationException("A generation-bound failure message cannot exceed 1000 characters.");
        }
        if (result.Failure.Message.Any(char.IsControl))
        {
            throw new InvalidOperationException("A generation-bound failure message cannot contain control characters.");
        }
        if (result.Items.Count != 0 || result.ContinuationToken is not null || result.Diagnostics.ReturnedCount != 0)
        {
            throw new InvalidOperationException("A failed generation-bound result cannot expose candidates or a continuation token.");
        }
    }

    public static void ValidateInspection(RecordSearchProjectionGenerationInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        RequireExact(inspection.Schema, RecordSearchProjectionGenerationSchemas.InspectionV1, "inspection schema");
        ValidateDescriptor(inspection.Descriptor);
        if (inspection.State is not RecordSearchProjectionGenerationStates.Active and
            not RecordSearchProjectionGenerationStates.Retained and
            not RecordSearchProjectionGenerationStates.Retired)
        {
            throw new InvalidOperationException("A generation inspection state must be active, retained, or retired.");
        }
        RequireCanonicalSet(inspection.AvailablePartitions, "availablePartitions", requireNonEmpty: false);
        if (inspection.AvailablePartitions.Except(inspection.Descriptor.ExpectedPartitions, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("availablePartitions must be a subset of expectedPartitions.");
        }
        if (inspection.State == RecordSearchProjectionGenerationStates.Retired && inspection.AvailablePartitions.Count != 0)
        {
            throw new InvalidOperationException("A retired generation cannot report available partitions.");
        }
        var expectedCoverage = inspection.State == RecordSearchProjectionGenerationStates.Retired
            ? RecordSearchProjectionCoverageStatuses.Unavailable
            : inspection.AvailablePartitions.SequenceEqual(inspection.Descriptor.ExpectedPartitions, StringComparer.Ordinal)
                ? RecordSearchProjectionCoverageStatuses.Complete
                : RecordSearchProjectionCoverageStatuses.Incomplete;
        if (!string.Equals(expectedCoverage, inspection.CoverageStatus, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("coverageStatus does not match the inspected partition evidence and lifecycle state.");
        }
        if (inspection.ObservedAtUtc == default || inspection.ObservedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException("observedAtUtc must be a non-default UTC timestamp.");
        }
    }

    public static void ValidateBuildRequest(RecordSearchProjectionGenerationBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireExact(request.Schema, RecordSearchProjectionGenerationSchemas.BuildRequestV1, "build request schema");
        RequireIdentifier(request.Collection, "collection");
        RequireIdentifier(request.GenerationId, "generationId");
        RequireIdentifier(request.BuilderId, "builderId");
        RequireIdentifier(request.ProviderId, "providerId");
        RequireIdentifier(request.ProfileId, "profileId");
        RequireIdentifier(request.StrategyVersion, "strategyVersion");
        RequireIdentifier(request.SourceManifestRef, "sourceManifestRef");
        if (request.SourceManifestRef.Contains('?') || request.SourceManifestRef.Contains('#'))
        {
            throw new InvalidOperationException("sourceManifestRef must be a stable reference without query parameters or fragments.");
        }
        if (Uri.TryCreate(request.SourceManifestRef, UriKind.Absolute, out var sourceUri) &&
            !string.IsNullOrEmpty(sourceUri.UserInfo))
        {
            throw new InvalidOperationException("sourceManifestRef cannot contain URI credentials.");
        }
        RequireSha256(request.SourceManifestDigest, "sourceManifestDigest");
        if (request.ExpectedRecordRevisionSetDigest is not null)
        {
            RequireSha256(request.ExpectedRecordRevisionSetDigest, "expectedRecordRevisionSetDigest");
        }
        RequireSha256(request.ProjectionSchemaDigest, "projectionSchemaDigest");
        if (request.AnalyzerDigest is not null)
        {
            RequireSha256(request.AnalyzerDigest, "analyzerDigest");
        }
        RequireSha256(request.ConfigurationDigest, "configurationDigest");
        if (request.ExpectedItemCount < 0)
        {
            throw new InvalidOperationException("expectedItemCount cannot be negative.");
        }
        RequireCanonicalSet(request.ExpectedPartitions, "expectedPartitions", requireNonEmpty: true);
        if (request.DeadlineUtc is { } deadline && deadline.Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException("deadlineUtc must be UTC when supplied.");
        }
    }

    public static void ValidateBuildReceipt(
        RecordSearchProjectionGenerationBuildRequest request,
        RecordSearchProjectionGenerationBuildReceipt receipt)
    {
        ValidateBuildRequest(request);
        ArgumentNullException.ThrowIfNull(receipt);
        RequireExact(receipt.Schema, RecordSearchProjectionGenerationSchemas.BuildReceiptV1, "build receipt schema");
        RequireExact(receipt.Status, RecordSearchProjectionBuildStatuses.Verified, "build receipt status");
        RequireExact(receipt.BuilderId, request.BuilderId, "build receipt builderId");
        ValidateDescriptor(receipt.Descriptor);
        if (!string.Equals(receipt.Descriptor.Collection, request.Collection, StringComparison.Ordinal) ||
            !string.Equals(receipt.Descriptor.GenerationId, request.GenerationId, StringComparison.Ordinal) ||
            !string.Equals(receipt.Descriptor.ProviderId, request.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(receipt.Descriptor.ProfileId, request.ProfileId, StringComparison.Ordinal) ||
            !string.Equals(receipt.Descriptor.StrategyVersion, request.StrategyVersion, StringComparison.Ordinal) ||
            !string.Equals(receipt.Descriptor.SourceManifestDigest, request.SourceManifestDigest, StringComparison.Ordinal) ||
            !string.Equals(receipt.Descriptor.ProjectionSchemaDigest, request.ProjectionSchemaDigest, StringComparison.Ordinal) ||
            !string.Equals(receipt.Descriptor.AnalyzerDigest, request.AnalyzerDigest, StringComparison.Ordinal) ||
            !string.Equals(receipt.Descriptor.ConfigurationDigest, request.ConfigurationDigest, StringComparison.Ordinal) ||
            receipt.Descriptor.ExpectedItemCount != request.ExpectedItemCount ||
            !receipt.Descriptor.ExpectedPartitions.SequenceEqual(request.ExpectedPartitions, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The verified descriptor does not match its admitted build request.");
        }
        if (request.ExpectedRecordRevisionSetDigest is not null &&
            !string.Equals(receipt.Descriptor.RecordRevisionSetDigest, request.ExpectedRecordRevisionSetDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The verified record/revision-set digest does not match the admitted expectation.");
        }
        if (receipt.EvaluationReceiptDigest is not null)
        {
            RequireSha256(receipt.EvaluationReceiptDigest, "evaluationReceiptDigest");
        }
        if (receipt.BuiltAtUtc == default || receipt.BuiltAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException("builtAtUtc must be a non-default UTC timestamp.");
        }
        if (receipt.Descriptor.CreatedAtUtc > receipt.BuiltAtUtc)
        {
            throw new InvalidOperationException("A descriptor cannot be created after its build receipt.");
        }
        if (request.DeadlineUtc is { } deadline && receipt.BuiltAtUtc > deadline)
        {
            throw new InvalidOperationException("A build receipt completed after its admitted deadline.");
        }
    }

    public static void ValidateBuildProgress(RecordSearchProjectionGenerationBuildProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        RequireIdentifier(progress.Stage, "build progress stage");
        if (progress.Completed < 0 || progress.Total < 0)
        {
            throw new InvalidOperationException("Projection generation progress counts cannot be negative.");
        }
        if (progress.Total is { } total && progress.Completed > total)
        {
            throw new InvalidOperationException("Projection generation completed work cannot exceed total work.");
        }
        if (progress.Checkpoint is not null)
        {
            ValidateBoundedSafeDetails(progress.Checkpoint, "Projection generation checkpoint");
        }
    }

    private static void ValidateCoverage(RecordSearchProjectionCoverage coverage)
    {
        if (coverage is null)
        {
            throw new InvalidOperationException("A generation-bound result requires coverage evidence.");
        }
        if (coverage.Status is not RecordSearchProjectionCoverageStatuses.Complete and
            not RecordSearchProjectionCoverageStatuses.Incomplete and
            not RecordSearchProjectionCoverageStatuses.Unavailable)
        {
            throw new InvalidOperationException("Coverage status must be complete, incomplete, or unavailable.");
        }
        RequireCanonicalSet(coverage.RequestedPartitions, "requestedPartitions", requireNonEmpty: false);
        RequireCanonicalSet(coverage.CoveredPartitions, "coveredPartitions", requireNonEmpty: false);
        RequireCanonicalSet(coverage.MissingPartitions, "missingPartitions", requireNonEmpty: false);
        if (coverage.CoveredPartitions.Except(coverage.RequestedPartitions, StringComparer.Ordinal).Any() ||
            coverage.MissingPartitions.Except(coverage.RequestedPartitions, StringComparer.Ordinal).Any() ||
            coverage.CoveredPartitions.Intersect(coverage.MissingPartitions, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("Coverage partitions must be disjoint subsets of requestedPartitions.");
        }
        var union = coverage.CoveredPartitions
            .Concat(coverage.MissingPartitions)
            .OrderBy(value => value, StringComparer.Ordinal);
        if (!union.SequenceEqual(coverage.RequestedPartitions, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Covered and missing partitions must account for every requested partition.");
        }
        if (coverage.Status == RecordSearchProjectionCoverageStatuses.Complete && coverage.MissingPartitions.Count != 0)
        {
            throw new InvalidOperationException("Complete coverage cannot include missing partitions.");
        }
        if (coverage.Status == RecordSearchProjectionCoverageStatuses.Incomplete && coverage.MissingPartitions.Count == 0)
        {
            throw new InvalidOperationException("Incomplete coverage requires at least one missing partition.");
        }
        if (coverage.Status == RecordSearchProjectionCoverageStatuses.Unavailable && coverage.CoveredPartitions.Count != 0)
        {
            throw new InvalidOperationException("Unavailable coverage cannot claim covered partitions.");
        }
    }

    private static void ValidateDiagnostics(RecordSearchProjectionWorkDiagnostics diagnostics)
    {
        if (diagnostics is null)
        {
            throw new InvalidOperationException("A generation-bound result requires bounded diagnostics.");
        }
        foreach (var (name, value) in new[]
        {
            ("workLimit", diagnostics.WorkLimit),
            ("workUnits", diagnostics.WorkUnits),
            ("candidateBound", diagnostics.CandidateBound),
            ("candidateCount", diagnostics.CandidateCount),
            ("returnedCount", (long?)diagnostics.ReturnedCount)
        })
        {
            if (value < 0)
            {
                throw new InvalidOperationException($"{name} cannot be negative.");
            }
        }
        if (diagnostics.CacheStatus is not RecordSearchProjectionCacheStatuses.Hit and
            not RecordSearchProjectionCacheStatuses.Miss and
            not RecordSearchProjectionCacheStatuses.Bypass and
            not RecordSearchProjectionCacheStatuses.NotApplicable)
        {
            throw new InvalidOperationException("cacheStatus is not recognized.");
        }
        if (diagnostics.CandidateCount is { } candidateCount && candidateCount < diagnostics.ReturnedCount)
        {
            throw new InvalidOperationException("candidateCount cannot be less than returnedCount.");
        }
        if (diagnostics.CandidateBound is { } candidateBound &&
            diagnostics.CandidateCount is { } boundedCandidateCount &&
            candidateBound < boundedCandidateCount)
        {
            throw new InvalidOperationException("candidateBound cannot be less than candidateCount.");
        }
        if (diagnostics.Details is not null)
        {
            ValidateBoundedSafeDetails(diagnostics.Details, "Projection diagnostics details");
        }
    }

    private static void ValidateBoundedSafeDetails(JsonObject details, string label)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(details, JsonOptions);
        if (bytes.Length > 16_384)
        {
            throw new InvalidOperationException($"{label} cannot exceed 16384 UTF-8 bytes.");
        }
        ValidateSafePropertyNames(details, label);
    }

    private static void ValidateSafePropertyNames(JsonNode node, string label)
    {
        if (node is JsonObject objectNode)
        {
            foreach (var property in objectNode)
            {
                if (IsSensitiveName(property.Key))
                {
                    throw new InvalidOperationException($"{label} cannot include sensitive field '{property.Key}'.");
                }
                if (property.Value is not null)
                {
                    ValidateSafePropertyNames(property.Value, label);
                }
            }
            return;
        }
        if (node is JsonArray arrayNode)
        {
            foreach (var item in arrayNode)
            {
                if (item is not null)
                {
                    ValidateSafePropertyNames(item, label);
                }
            }
        }
    }

    private static void ValidateCandidates(IEnumerable<RecordSearchProjectionCandidate> candidates)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            RequireIdentifier(candidate.PartitionKey, "candidate partitionKey");
            RequireIdentifier(candidate.Id, "candidate id");
            if (candidate.Revision <= 0 || !float.IsFinite(candidate.Score))
            {
                throw new InvalidOperationException("Projection candidates require a positive revision and finite score.");
            }
            if (!identities.Add(candidate.PartitionKey + "\n" + candidate.Id))
            {
                throw new InvalidOperationException("Projection candidates must have unique partition/id identities.");
            }
        }
    }

    private static void RequireCanonicalSet(IReadOnlyCollection<string> values, string name, bool requireNonEmpty)
    {
        if (requireNonEmpty && values.Count == 0)
        {
            throw new InvalidOperationException($"{name} must not be empty.");
        }
        var canonical = values.OrderBy(value => value, StringComparer.Ordinal).ToList();
        foreach (var value in canonical)
        {
            RequireIdentifier(value, name + " item");
        }
        if (canonical.Distinct(StringComparer.Ordinal).Count() != canonical.Count)
        {
            throw new InvalidOperationException($"{name} must contain unique non-empty values.");
        }
        if (!canonical.SequenceEqual(values, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{name} must use canonical ordinal ordering.");
        }
    }

    private static void RequireIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"{name} must be a trimmed non-empty value no longer than 200 characters.");
        }
    }

    private static bool IsSensitiveName(string value) =>
        value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("cookie", StringComparison.OrdinalIgnoreCase);

    private static void RequireExact(string? actual, string expected, string name)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{name} must be '{expected}'.");
        }
    }

    private static void RequireSha256(string? value, string name)
    {
        var valid = value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal);
        if (valid)
        {
            foreach (var character in value!.AsSpan(7))
            {
                if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                {
                    valid = false;
                    break;
                }
            }
        }
        if (!valid)
        {
            throw new InvalidOperationException($"{name} must be a lowercase sha256 digest.");
        }
    }

    private static string Hash(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
