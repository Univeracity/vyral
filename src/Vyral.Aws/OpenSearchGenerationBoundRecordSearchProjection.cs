using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Aws;

public static class OpenSearchProjectionGenerationBinding
{
    public const string ArtifactId = "provider-generation";
    public const string ArtifactKind = "opensearchIndexBinding";
    private const string BindingVersion = "vyral.aws.opensearch-index-binding.v1";

    /// <summary>
    /// Binds an opaque provider index coordinate to a portable descriptor without disclosing the
    /// coordinate in the descriptor. The adapter independently verifies the same material before
    /// it accepts candidates.
    /// </summary>
    public static string ComputeContentHash(string indexName, string indexUuid)
    {
        indexName = OpenSearchRecordSearchProjectionOptions.ValidateIndexName(indexName);
        ValidateIndexUuid(indexUuid);
        var material = $"{BindingVersion}\n{indexName}\n{indexUuid}";
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>
    /// Computes the exact policy material that controls the OpenSearch projection mapping and
    /// provider query field names. Retained generations must be queried with the policy they were
    /// built from, not whichever policy is current when the request arrives.
    /// </summary>
    public static string ComputeProjectionSchemaDigest(RecordCollectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        RecordIdentityValidator.ValidateCollectionName(policy.Name);
        var material = new
        {
            schema = "vyral.aws.opensearch-projection-schema.v1",
            collection = policy.Name,
            indexedMetadata = policy.IndexedMetadata.OrderBy(value => value, StringComparer.Ordinal),
            vectors = policy.VectorPolicies
                .OrderBy(value => value.Name, StringComparer.Ordinal)
                .ThenBy(value => value.Path, StringComparer.Ordinal)
                .Select(value => new
                {
                    name = value.Name,
                    path = value.Path,
                    dimensions = value.Dimensions,
                    datatype = value.Datatype,
                    distanceFunction = value.DistanceFunction,
                    indexType = value.IndexType
                })
        };
        return "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(material)));
    }

    internal static void ValidateIndexUuid(string? indexUuid)
    {
        if (string.IsNullOrWhiteSpace(indexUuid) || indexUuid.Length > 200 ||
            !string.Equals(indexUuid, indexUuid.Trim(), StringComparison.Ordinal) ||
            indexUuid.Any(char.IsControl))
        {
            throw new InvalidOperationException("An OpenSearch generation requires a bounded index UUID.");
        }
    }
}

public sealed class OpenSearchRecordSearchProjectionGeneration
{
    public RecordSearchProjectionGenerationDescriptor Descriptor { get; set; } = new();
    public string IndexName { get; set; } = string.Empty;
    public string IndexUuid { get; set; } = string.Empty;
    public string State { get; set; } = RecordSearchProjectionGenerationStates.Retained;
    public List<string>? AvailablePartitions { get; set; }
}

public sealed class OpenSearchGenerationBoundRecordSearchProjectionOptions
{
    public const string DefaultProviderId = "aws-opensearch";
    public string ProviderId { get; set; } = DefaultProviderId;
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>
/// Exact-generation, candidate-only OpenSearch adapter. Hosts register immutable provider indexes
/// from verified build evidence; callers can select only those allowlisted registrations, never an
/// arbitrary index or endpoint. Every search verifies the index UUID, read-only block, non-timeout
/// response, complete shard participation, and exact hit index before exposing candidates.
/// </summary>
public sealed class OpenSearchGenerationBoundRecordSearchProjection : IGenerationBoundRecordSearchProjection
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IOpenSearchTransport _transport;
    private readonly OpenSearchRecordSearchProjection _searchProjection;
    private readonly OpenSearchGenerationBoundRecordSearchProjectionOptions _options;
    private readonly int _maximumCandidates;
    private readonly Dictionary<string, Dictionary<string, Registration>> _collections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeGenerations = new(StringComparer.Ordinal);

    public OpenSearchGenerationBoundRecordSearchProjection(
        IOpenSearchTransport transport,
        OpenSearchRecordSearchProjectionOptions searchOptions,
        IEnumerable<OpenSearchRecordSearchProjectionGeneration> generations,
        OpenSearchGenerationBoundRecordSearchProjectionOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(searchOptions);
        ArgumentNullException.ThrowIfNull(generations);
        var configuredOptions = options ?? new OpenSearchGenerationBoundRecordSearchProjectionOptions();
        if (string.IsNullOrWhiteSpace(configuredOptions.ProviderId) || configuredOptions.ProviderId.Length > 200 ||
            !string.Equals(configuredOptions.ProviderId, configuredOptions.ProviderId.Trim(), StringComparison.Ordinal) ||
            configuredOptions.ProviderId.Any(char.IsControl) || configuredOptions.TimeProvider is null)
        {
            throw new InvalidOperationException("The OpenSearch generation options are invalid.");
        }
        _options = new OpenSearchGenerationBoundRecordSearchProjectionOptions
        {
            ProviderId = configuredOptions.ProviderId,
            TimeProvider = configuredOptions.TimeProvider
        };
        _searchProjection = new OpenSearchRecordSearchProjection(
            transport,
            new OpenSearchRecordSearchProjectionOptions { MaximumCandidates = searchOptions.MaximumCandidates });
        _maximumCandidates = searchOptions.MaximumCandidates;

        foreach (var generation in generations)
        {
            Register(generation);
        }
    }

    public async Task<RecordSearchProjectionGenerationInspection?> InspectGenerationAsync(
        RecordCollectionPolicy policy,
        string? generationId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var registration = Resolve(policy.Name, generationId);
        if (registration is null)
        {
            return null;
        }

        var available = registration.State == RecordSearchProjectionGenerationStates.Retired
            ? new List<string>()
            : PolicyMatchesDescriptor(policy, registration.Descriptor) &&
              await VerifyRemoteBindingAsync(registration, ct)
                ? registration.AvailablePartitions.ToList()
                : new List<string>();
        var inspection = new RecordSearchProjectionGenerationInspection
        {
            Descriptor = CloneDescriptor(registration.Descriptor),
            State = registration.State,
            AvailablePartitions = available,
            CoverageStatus = registration.State == RecordSearchProjectionGenerationStates.Retired
                ? RecordSearchProjectionCoverageStatuses.Unavailable
                : available.SequenceEqual(registration.Descriptor.ExpectedPartitions, StringComparer.Ordinal)
                    ? RecordSearchProjectionCoverageStatuses.Complete
                    : RecordSearchProjectionCoverageStatuses.Incomplete,
            ObservedAtUtc = UtcNow()
        };
        RecordSearchProjectionGenerationContract.ValidateInspection(inspection);
        return inspection;
    }

    public async Task<GenerationBoundRecordSearchProjectionResult> SearchGenerationAsync(
        RecordCollectionPolicy policy,
        GenerationBoundRecordSearchProjectionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        RecordSearchProjectionGenerationContract.ValidateRequest(request);
        ct.ThrowIfCancellationRequested();

        var requestedWithoutDescriptor = CanonicalPartitions(request.Query.PartitionKeys ?? new List<string>());
        if (!string.IsNullOrWhiteSpace(request.Query.ContinuationToken))
        {
            return Failure(
                request,
                null,
                RecordSearchProjectionCoverageStatuses.Unavailable,
                RecordSearchProjectionFailureCodes.InvalidContinuation,
                "This OpenSearch generation path does not issue pageable vector continuations.",
                retryable: false,
                requestedWithoutDescriptor);
        }

        var registration = Resolve(policy.Name, request.GenerationId);
        if (registration is null)
        {
            return Failure(
                request,
                null,
                RecordSearchProjectionCoverageStatuses.Unavailable,
                RecordSearchProjectionFailureCodes.GenerationUnavailable,
                "The requested generation is unavailable.",
                retryable: true,
                requestedWithoutDescriptor,
                generationId: request.GenerationId);
        }

        var requested = request.Query.PartitionKeys is { Count: > 0 }
            ? CanonicalPartitions(request.Query.PartitionKeys)
            : registration.Descriptor.ExpectedPartitions.ToList();
        if (registration.State == RecordSearchProjectionGenerationStates.Retired)
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Unavailable,
                RecordSearchProjectionFailureCodes.GenerationRetired,
                "The requested generation has been retired.",
                retryable: false,
                requested);
        }
        if (!PolicyMatchesDescriptor(policy, registration.Descriptor))
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.GenerationDescriptorMismatch,
                "The collection policy does not match the selected generation's projection schema.",
                retryable: false,
                requested,
                requested);
        }
        if (request.ExpectedDescriptorDigest is not null &&
            !string.Equals(request.ExpectedDescriptorDigest, registration.Descriptor.DescriptorDigest, StringComparison.Ordinal))
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.GenerationDescriptorMismatch,
                "The selected generation descriptor does not match the caller's expected digest.",
                retryable: false,
                requested,
                requested);
        }

        var covered = requested
            .Intersect(registration.AvailablePartitions, StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        if (covered.Count != requested.Count)
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Incomplete,
                RecordSearchProjectionFailureCodes.CoverageIncomplete,
                "One or more requested partitions are not covered by the selected generation.",
                retryable: true,
                requested,
                covered);
        }
        if (request.DeadlineUtc is { } deadline && deadline <= UtcNow())
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.DeadlineExceeded,
                "The generation-bound search deadline has elapsed.",
                retryable: true,
                requested,
                covered);
        }
        if (request.Query.Vector is null || request.Query.Lexical is not null || request.Query.OrderBy is { Count: > 0 })
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.UnsupportedQuery,
                "This OpenSearch generation path supports bounded vector candidate retrieval only.",
                retryable: false,
                requested,
                covered);
        }
        try
        {
            FilterValueNormalizer.ValidateFilter(request.Query.Filter);
            RecordVectorValidator.ValidateSearchVector(policy.Name, policy, request.Query.Vector);
            var requestedCandidates = request.Query.Limit ?? request.Query.Vector.Top;
            if (request.Query.Vector.MinScore.HasValue || request.Query.Vector.Top <= 0 || requestedCandidates <= 0 ||
                request.Query.Vector.Top > _maximumCandidates || requestedCandidates > _maximumCandidates)
            {
                throw new NotSupportedException();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.UnsupportedQuery,
                "The request uses query behavior that is not supported by this OpenSearch generation.",
                retryable: false,
                requested,
                covered);
        }

        RecordSearchProjectionResult candidates;
        using var deadlineSource = CreateDeadlineSource(request.DeadlineUtc, ct);
        var remoteCancellation = deadlineSource?.Token ?? ct;
        try
        {
            if (!await VerifyRemoteBindingAsync(registration, remoteCancellation))
            {
                return Failure(
                    request,
                    registration,
                    RecordSearchProjectionCoverageStatuses.Incomplete,
                    RecordSearchProjectionFailureCodes.CoverageIncomplete,
                    "OpenSearch did not prove the selected immutable generation binding.",
                    retryable: true,
                    requested);
            }
            candidates = await _searchProjection.SearchIndexAsync(
                policy,
                request.Query,
                registration.IndexName,
                requireCompleteResponse: true,
                remoteCancellation);
            if (!CandidatesAreBounded(candidates.Items, requested) ||
                !await VerifyRemoteBindingAsync(registration, remoteCancellation))
            {
                return Failure(
                    request,
                    registration,
                    RecordSearchProjectionCoverageStatuses.Incomplete,
                    RecordSearchProjectionFailureCodes.CoverageIncomplete,
                    "OpenSearch did not preserve the bounded immutable generation through search completion.",
                    retryable: true,
                    requested);
            }
            if (request.DeadlineUtc is { } completedDeadline && completedDeadline <= UtcNow())
            {
                return Failure(
                    request,
                    registration,
                    RecordSearchProjectionCoverageStatuses.Complete,
                    RecordSearchProjectionFailureCodes.DeadlineExceeded,
                    "The generation-bound search deadline elapsed before OpenSearch completed.",
                    retryable: true,
                    requested,
                    covered);
            }
        }
        catch (NotSupportedException)
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.UnsupportedQuery,
                "The request uses query behavior that is not supported by this OpenSearch generation.",
                retryable: false,
                requested,
                covered);
        }
        catch (InvalidOperationException)
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Incomplete,
                RecordSearchProjectionFailureCodes.CoverageIncomplete,
                "OpenSearch did not prove a complete generation-bound search.",
                retryable: true,
                requested);
        }
        catch (HttpRequestException)
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Incomplete,
                RecordSearchProjectionFailureCodes.CoverageIncomplete,
                "OpenSearch did not prove a complete generation-bound search.",
                retryable: true,
                requested);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Incomplete,
                RecordSearchProjectionFailureCodes.CoverageIncomplete,
                "OpenSearch returned a malformed generation-bound search response.",
                retryable: true,
                requested);
        }
        catch (OperationCanceledException) when (
            request.DeadlineUtc.HasValue && !ct.IsCancellationRequested &&
            deadlineSource?.IsCancellationRequested == true)
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.DeadlineExceeded,
                "The generation-bound search deadline elapsed before OpenSearch completed.",
                retryable: true,
                requested,
                covered);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Incomplete,
                RecordSearchProjectionFailureCodes.CoverageIncomplete,
                "OpenSearch did not complete the generation-bound search.",
                retryable: true,
                requested);
        }

        var candidateBound = Math.Max(request.Query.Vector.Top, request.Query.Limit ?? request.Query.Vector.Top);
        var result = new GenerationBoundRecordSearchProjectionResult
        {
            Status = RecordSearchProjectionResultStatuses.Succeeded,
            GenerationId = registration.Descriptor.GenerationId,
            GenerationDescriptorDigest = registration.Descriptor.DescriptorDigest,
            Items = candidates.Items,
            Coverage = new RecordSearchProjectionCoverage
            {
                Status = RecordSearchProjectionCoverageStatuses.Complete,
                RequestedPartitions = requested,
                CoveredPartitions = covered
            },
            Diagnostics = new RecordSearchProjectionWorkDiagnostics
            {
                CandidateBound = candidateBound,
                CandidateCount = candidates.Items.Count,
                ReturnedCount = candidates.Items.Count,
                CacheStatus = RecordSearchProjectionCacheStatuses.NotApplicable
            }
        };
        try
        {
            RecordSearchProjectionGenerationContract.ValidateResult(result);
            return result;
        }
        catch (InvalidOperationException)
        {
            return Failure(
                request,
                registration,
                RecordSearchProjectionCoverageStatuses.Incomplete,
                RecordSearchProjectionFailureCodes.CoverageIncomplete,
                "OpenSearch returned candidates that do not satisfy the portable projection contract.",
                retryable: true,
                requested);
        }
    }

    private void Register(OpenSearchRecordSearchProjectionGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        RecordSearchProjectionGenerationContract.ValidateDescriptor(generation.Descriptor);
        if (!string.Equals(generation.Descriptor.ProviderId, _options.ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An OpenSearch generation descriptor has an unexpected provider ID.");
        }
        var requiredCapabilities = new[]
        {
            RecordSearchProjectionGenerationCapabilities.CompleteCoverage,
            RecordSearchProjectionGenerationCapabilities.GenerationPinnedContinuation,
            RecordSearchProjectionGenerationCapabilities.Vector
        };
        if (requiredCapabilities.Except(generation.Descriptor.Capabilities, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException(
                "An OpenSearch generation descriptor must declare complete coverage, generation-pinned continuation safety, and vector capability.");
        }
        if (generation.State is not RecordSearchProjectionGenerationStates.Active and
            not RecordSearchProjectionGenerationStates.Retained and
            not RecordSearchProjectionGenerationStates.Retired)
        {
            throw new InvalidOperationException("An OpenSearch generation state must be active, retained, or retired.");
        }
        var indexName = OpenSearchRecordSearchProjectionOptions.ValidateIndexName(generation.IndexName);
        OpenSearchProjectionGenerationBinding.ValidateIndexUuid(generation.IndexUuid);
        var binding = generation.Descriptor.Artifacts.SingleOrDefault(artifact =>
            string.Equals(artifact.Id, OpenSearchProjectionGenerationBinding.ArtifactId, StringComparison.Ordinal) &&
            string.Equals(artifact.Kind, OpenSearchProjectionGenerationBinding.ArtifactKind, StringComparison.Ordinal));
        var expectedHash = OpenSearchProjectionGenerationBinding.ComputeContentHash(indexName, generation.IndexUuid);
        if (binding is null || !string.Equals(binding.ContentHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The OpenSearch index coordinate is not bound to the generation descriptor.");
        }

        IEnumerable<string> availableSource = generation.State == RecordSearchProjectionGenerationStates.Retired
            ? Array.Empty<string>()
            : generation.AvailablePartitions ?? generation.Descriptor.ExpectedPartitions;
        var available = availableSource
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        if (available.Distinct(StringComparer.Ordinal).Count() != available.Count ||
            available.Except(generation.Descriptor.ExpectedPartitions, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("OpenSearch available partitions must be a unique descriptor subset.");
        }
        if (generation.State == RecordSearchProjectionGenerationStates.Active &&
            !available.SequenceEqual(generation.Descriptor.ExpectedPartitions, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("An incomplete OpenSearch generation cannot be active.");
        }

        if (!_collections.TryGetValue(generation.Descriptor.Collection, out var collection))
        {
            collection = new Dictionary<string, Registration>(StringComparer.Ordinal);
            _collections.Add(generation.Descriptor.Collection, collection);
        }
        if (!collection.TryAdd(generation.Descriptor.GenerationId, new Registration(
                CloneDescriptor(generation.Descriptor), indexName, generation.IndexUuid, generation.State, available)))
        {
            throw new InvalidOperationException("An OpenSearch generation ID can be registered only once.");
        }
        if (generation.State == RecordSearchProjectionGenerationStates.Active &&
            !_activeGenerations.TryAdd(generation.Descriptor.Collection, generation.Descriptor.GenerationId))
        {
            throw new InvalidOperationException("A collection can register only one active OpenSearch generation.");
        }
    }

    private Registration? Resolve(string collection, string? generationId)
    {
        if (!_collections.TryGetValue(collection, out var generations))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(generationId) &&
            !_activeGenerations.TryGetValue(collection, out generationId))
        {
            return null;
        }
        return generations.GetValueOrDefault(generationId!);
    }

    private async Task<bool> VerifyRemoteBindingAsync(Registration registration, CancellationToken ct)
    {
        if (registration.State == RecordSearchProjectionGenerationStates.Retired)
        {
            return false;
        }
        try
        {
            var response = await _transport.SendAsync(
                HttpMethod.Get,
                $"/{registration.IndexName}/_settings?flat_settings=true",
                null,
                ct);
            if (response.StatusCode is < 200 or >= 300)
            {
                return false;
            }
            using var document = JsonDocument.Parse(response.Body);
            if (!document.RootElement.TryGetProperty(registration.IndexName, out var index) ||
                !index.TryGetProperty("settings", out var settings))
            {
                return false;
            }
            var uuid = ReadSetting(settings, "index.uuid", "uuid");
            var readOnlyBlock = ReadSetting(settings, "index.blocks.read_only", "blocks", "read_only");
            return uuid is not null &&
                   string.Equals(uuid, registration.IndexUuid, StringComparison.Ordinal) &&
                   string.Equals(readOnlyBlock, "true", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       OpenSearchProjectionGenerationBinding.ComputeContentHash(registration.IndexName, uuid),
                       registration.BindingContentHash,
                       StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string? ReadSetting(JsonElement settings, string flatName, params string[] nestedPath)
    {
        if (settings.TryGetProperty(flatName, out var flat) && flat.ValueKind == JsonValueKind.String)
        {
            return flat.GetString();
        }
        var current = settings;
        if (current.TryGetProperty("index", out var index))
        {
            current = index;
        }
        foreach (var segment in nestedPath)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private GenerationBoundRecordSearchProjectionResult Failure(
        GenerationBoundRecordSearchProjectionRequest request,
        Registration? registration,
        string coverageStatus,
        string code,
        string message,
        bool retryable,
        List<string> requested,
        List<string>? covered = null,
        string? generationId = null)
    {
        covered = covered?.ToList() ?? new List<string>();
        if (coverageStatus == RecordSearchProjectionCoverageStatuses.Unavailable)
        {
            covered.Clear();
        }
        var result = new GenerationBoundRecordSearchProjectionResult
        {
            Status = RecordSearchProjectionResultStatuses.Failed,
            GenerationId = registration?.Descriptor.GenerationId ?? generationId,
            GenerationDescriptorDigest = registration?.Descriptor.DescriptorDigest,
            Coverage = new RecordSearchProjectionCoverage
            {
                Status = coverageStatus,
                RequestedPartitions = requested,
                CoveredPartitions = covered,
                MissingPartitions = requested.Except(covered, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList()
            },
            Diagnostics = new RecordSearchProjectionWorkDiagnostics(),
            Failure = new RecordSearchProjectionFailure { Code = code, Message = message, Retryable = retryable }
        };
        RecordSearchProjectionGenerationContract.ValidateResult(result);
        return result;
    }

    private static List<string> CanonicalPartitions(IEnumerable<string> partitions) =>
        partitions.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();

    private static bool PolicyMatchesDescriptor(
        RecordCollectionPolicy policy,
        RecordSearchProjectionGenerationDescriptor descriptor) =>
        string.Equals(
            OpenSearchProjectionGenerationBinding.ComputeProjectionSchemaDigest(policy),
            descriptor.ProjectionSchemaDigest,
            StringComparison.Ordinal);

    private static bool CandidatesAreBounded(
        IEnumerable<RecordSearchProjectionCandidate> candidates,
        IReadOnlyCollection<string> requestedPartitions)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!requestedPartitions.Contains(candidate.PartitionKey, StringComparer.Ordinal) ||
                candidate.Revision <= 0 || !float.IsFinite(candidate.Score) ||
                !identities.Add(candidate.PartitionKey + "\n" + candidate.Id))
            {
                return false;
            }
        }
        return true;
    }

    private CancellationTokenSource? CreateDeadlineSource(DateTime? deadlineUtc, CancellationToken ct)
    {
        if (deadlineUtc is null)
        {
            return null;
        }
        var remaining = deadlineUtc.Value - UtcNow();
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        return source;
    }

    private DateTime UtcNow() => _options.TimeProvider.GetUtcNow().UtcDateTime;

    private static RecordSearchProjectionGenerationDescriptor CloneDescriptor(
        RecordSearchProjectionGenerationDescriptor descriptor) =>
        JsonSerializer.Deserialize<RecordSearchProjectionGenerationDescriptor>(
            JsonSerializer.Serialize(descriptor, JsonOptions),
            JsonOptions) ?? throw new InvalidOperationException("Could not clone an OpenSearch generation descriptor.");

    private sealed class Registration
    {
        public Registration(
            RecordSearchProjectionGenerationDescriptor descriptor,
            string indexName,
            string indexUuid,
            string state,
            List<string> availablePartitions)
        {
            Descriptor = descriptor;
            IndexName = indexName;
            IndexUuid = indexUuid;
            State = state;
            AvailablePartitions = availablePartitions;
            BindingContentHash = OpenSearchProjectionGenerationBinding.ComputeContentHash(indexName, indexUuid);
        }

        public RecordSearchProjectionGenerationDescriptor Descriptor { get; }
        public string IndexName { get; }
        public string IndexUuid { get; }
        public string State { get; }
        public List<string> AvailablePartitions { get; }
        public string BindingContentHash { get; }
    }
}
