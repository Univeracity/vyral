using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

public sealed class LocalGenerationBoundRecordSearchProjectionOptions
{
    public byte[] ContinuationSigningKey { get; set; } = Array.Empty<byte>();
    public TimeSpan ContinuationLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public int DefaultResultLimit { get; set; } = 50;
    public int MaxResultLimit { get; set; } = 1000;
    public int DefaultWorkLimit { get; set; } = 100_000;
    public int MaxWorkLimit { get; set; } = 1_000_000;
    public int MaxContinuationTokenChars { get; set; } = 8192;
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

public sealed class LocalRecordSearchProjectionDocument
{
    public RecordSearchProjectionCandidate Candidate { get; set; } = new();
    public string SearchText { get; set; } = string.Empty;
}

public sealed class LocalRecordSearchProjectionGeneration
{
    public RecordSearchProjectionGenerationDescriptor Descriptor { get; set; } = new();
    public List<LocalRecordSearchProjectionDocument> Documents { get; set; } = new();
    public List<string>? AvailablePartitions { get; set; }
}

/// <summary>
/// Deterministic exhaustive reference for the optional generation-bound projection contract. Its
/// local publication controls are deliberately not part of the portable interface: they exist to
/// exercise generation switching, retention, retirement, and coverage faults before a provider
/// lifecycle is standardized.
/// </summary>
public sealed class LocalGenerationBoundRecordSearchProjection : IGenerationBoundRecordSearchProjection
{
    private const string ContinuationVersion = "vyral.local-record-projection-continuation.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, GenerationEntry>> _collections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeGenerations = new(StringComparer.Ordinal);
    private readonly byte[] _signingKey;
    private readonly LocalGenerationBoundRecordSearchProjectionOptions _options;

    public LocalGenerationBoundRecordSearchProjection(LocalGenerationBoundRecordSearchProjectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ContinuationSigningKey is not { Length: >= 32 })
        {
            throw new InvalidOperationException("A local generation-bound projection requires a continuation signing key of at least 32 bytes.");
        }
        if (options.ContinuationLifetime <= TimeSpan.Zero || options.ContinuationLifetime > TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException("ContinuationLifetime must be greater than zero and no longer than one day.");
        }
        if (options.DefaultResultLimit <= 0 || options.MaxResultLimit < options.DefaultResultLimit ||
            options.MaxResultLimit > 10_000)
        {
            throw new InvalidOperationException("Result limits are invalid.");
        }
        if (options.DefaultWorkLimit <= 0 || options.MaxWorkLimit < options.DefaultWorkLimit ||
            options.MaxWorkLimit > 10_000_000)
        {
            throw new InvalidOperationException("Work limits are invalid.");
        }
        if (options.MaxContinuationTokenChars is < 256 or > 8192)
        {
            throw new InvalidOperationException("MaxContinuationTokenChars must be between 256 and 8192.");
        }
        if (options.TimeProvider is null)
        {
            throw new InvalidOperationException("A local generation-bound projection requires a time provider.");
        }
        _options = new LocalGenerationBoundRecordSearchProjectionOptions
        {
            ContinuationLifetime = options.ContinuationLifetime,
            DefaultResultLimit = options.DefaultResultLimit,
            MaxResultLimit = options.MaxResultLimit,
            DefaultWorkLimit = options.DefaultWorkLimit,
            MaxWorkLimit = options.MaxWorkLimit,
            MaxContinuationTokenChars = options.MaxContinuationTokenChars,
            TimeProvider = options.TimeProvider
        };
        _signingKey = options.ContinuationSigningKey.ToArray();
    }

    public void PublishGeneration(LocalRecordSearchProjectionGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        RecordSearchProjectionGenerationContract.ValidateDescriptor(generation.Descriptor);
        ValidateDocuments(generation);

        var available = (generation.AvailablePartitions ?? generation.Descriptor.ExpectedPartitions)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        if (available.Distinct(StringComparer.Ordinal).Count() != available.Count ||
            available.Except(generation.Descriptor.ExpectedPartitions, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("Available partitions must be a unique subset of descriptor expectedPartitions.");
        }

        var entry = new GenerationEntry(
            CloneDescriptor(generation.Descriptor),
            generation.Documents.Select(CloneDocument).ToList(),
            available,
            RecordSearchProjectionGenerationStates.Retained);
        lock (_gate)
        {
            if (!_collections.TryGetValue(generation.Descriptor.Collection, out var generations))
            {
                generations = new Dictionary<string, GenerationEntry>(StringComparer.Ordinal);
                _collections.Add(generation.Descriptor.Collection, generations);
            }
            if (generations.TryGetValue(generation.Descriptor.GenerationId, out var existing))
            {
                if (!string.Equals(existing.Descriptor.DescriptorDigest, generation.Descriptor.DescriptorDigest, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("An immutable generation ID cannot be republished with different descriptor evidence.");
                }
                if (!GenerationContentEquals(existing, entry))
                {
                    throw new InvalidOperationException("An immutable generation ID cannot be republished with different documents or coverage evidence.");
                }
                return;
            }
            generations.Add(generation.Descriptor.GenerationId, entry);
        }
    }

    public void ActivateGeneration(string collection, string generationId)
    {
        lock (_gate)
        {
            var entry = GetEntry(collection, generationId)
                ?? throw new InvalidOperationException("The generation is unavailable.");
            if (entry.State == RecordSearchProjectionGenerationStates.Retired)
            {
                throw new InvalidOperationException("A retired generation cannot be activated.");
            }
            if (!entry.AvailablePartitions.SequenceEqual(entry.Descriptor.ExpectedPartitions, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("An incomplete generation cannot be activated.");
            }
            if (_activeGenerations.TryGetValue(collection, out var priorId) &&
                GetEntry(collection, priorId) is { } prior)
            {
                prior.State = RecordSearchProjectionGenerationStates.Retained;
            }
            entry.State = RecordSearchProjectionGenerationStates.Active;
            _activeGenerations[collection] = generationId;
        }
    }

    public void RetireGeneration(string collection, string generationId)
    {
        lock (_gate)
        {
            var entry = GetEntry(collection, generationId)
                ?? throw new InvalidOperationException("The generation is unavailable.");
            if (_activeGenerations.TryGetValue(collection, out var active) &&
                string.Equals(active, generationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The active generation cannot be retired before another generation is activated.");
            }
            entry.State = RecordSearchProjectionGenerationStates.Retired;
            entry.AvailablePartitions.Clear();
        }
    }

    public void SetAvailablePartitions(string collection, string generationId, IEnumerable<string> partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        var available = partitions.OrderBy(value => value, StringComparer.Ordinal).ToList();
        lock (_gate)
        {
            var entry = GetEntry(collection, generationId)
                ?? throw new InvalidOperationException("The generation is unavailable.");
            if (entry.State == RecordSearchProjectionGenerationStates.Retired)
            {
                throw new InvalidOperationException("A retired generation cannot become available.");
            }
            if (available.Distinct(StringComparer.Ordinal).Count() != available.Count ||
                available.Except(entry.Descriptor.ExpectedPartitions, StringComparer.Ordinal).Any())
            {
                throw new InvalidOperationException("Available partitions must be a unique subset of descriptor expectedPartitions.");
            }
            entry.AvailablePartitions = available;
        }
    }

    public Task<RecordSearchProjectionGenerationInspection?> InspectGenerationAsync(
        RecordCollectionPolicy policy,
        string? generationId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ct.ThrowIfCancellationRequested();
        GenerationEntry? snapshot;
        lock (_gate)
        {
            var resolvedId = generationId;
            if (string.IsNullOrWhiteSpace(resolvedId) && !_activeGenerations.TryGetValue(policy.Name, out resolvedId))
            {
                return Task.FromResult<RecordSearchProjectionGenerationInspection?>(null);
            }
            snapshot = GetEntry(policy.Name, resolvedId!);
            if (snapshot is null)
            {
                return Task.FromResult<RecordSearchProjectionGenerationInspection?>(null);
            }
            snapshot = snapshot.Clone();
        }

        var coverage = snapshot.State == RecordSearchProjectionGenerationStates.Retired
            ? RecordSearchProjectionCoverageStatuses.Unavailable
            : snapshot.AvailablePartitions.SequenceEqual(snapshot.Descriptor.ExpectedPartitions, StringComparer.Ordinal)
                ? RecordSearchProjectionCoverageStatuses.Complete
                : RecordSearchProjectionCoverageStatuses.Incomplete;
        var inspection = new RecordSearchProjectionGenerationInspection
        {
            Descriptor = snapshot.Descriptor,
            State = snapshot.State,
            AvailablePartitions = snapshot.AvailablePartitions,
            CoverageStatus = coverage,
            ObservedAtUtc = UtcNow()
        };
        RecordSearchProjectionGenerationContract.ValidateInspection(inspection);
        return Task.FromResult<RecordSearchProjectionGenerationInspection?>(inspection);
    }

    public Task<GenerationBoundRecordSearchProjectionResult> SearchGenerationAsync(
        RecordCollectionPolicy policy,
        GenerationBoundRecordSearchProjectionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        RecordSearchProjectionGenerationContract.ValidateRequest(request);
        ct.ThrowIfCancellationRequested();
        var now = UtcNow();

        ContinuationPayload? continuation = null;
        if (!string.IsNullOrWhiteSpace(request.Query.ContinuationToken))
        {
            var continuationFailure = ReadContinuation(request.Query.ContinuationToken!, now, out continuation);
            if (continuationFailure is not null)
            {
                return Task.FromResult(Failure(
                    request,
                    null,
                    RecordSearchProjectionCoverageStatuses.Unavailable,
                    continuationFailure.Value.Code,
                    continuationFailure.Value.Message,
                    retryable: false));
            }
            if (request.GenerationId is not null && !string.Equals(request.GenerationId, continuation!.GenerationId, StringComparison.Ordinal))
            {
                return Task.FromResult(Failure(
                    request,
                    null,
                    RecordSearchProjectionCoverageStatuses.Unavailable,
                    RecordSearchProjectionFailureCodes.InvalidContinuation,
                    "The requested generation does not match the continuation generation.",
                    retryable: false));
            }
        }

        var generationId = continuation?.GenerationId ?? request.GenerationId;
        GenerationEntry? entry;
        lock (_gate)
        {
            if (generationId is null && !_activeGenerations.TryGetValue(policy.Name, out generationId))
            {
                return Task.FromResult(Failure(
                    request,
                    null,
                    RecordSearchProjectionCoverageStatuses.Unavailable,
                    RecordSearchProjectionFailureCodes.GenerationUnavailable,
                    "No active generation is available.",
                    retryable: true));
            }
            entry = GetEntry(policy.Name, generationId!)?.Clone();
        }

        if (entry is null)
        {
            return Task.FromResult(Failure(
                request,
                null,
                RecordSearchProjectionCoverageStatuses.Unavailable,
                RecordSearchProjectionFailureCodes.GenerationUnavailable,
                "The requested generation is unavailable.",
                retryable: true,
                generationId: generationId));
        }

        var requestedPartitions = ResolveRequestedPartitions(request.Query, entry.Descriptor);
        if (entry.State == RecordSearchProjectionGenerationStates.Retired)
        {
            return Task.FromResult(Failure(
                request,
                entry,
                RecordSearchProjectionCoverageStatuses.Unavailable,
                RecordSearchProjectionFailureCodes.GenerationRetired,
                "The requested generation has been retired.",
                retryable: false,
                requestedPartitions: requestedPartitions));
        }

        var coveredPartitions = requestedPartitions
            .Intersect(entry.AvailablePartitions, StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var missingPartitions = requestedPartitions
            .Except(coveredPartitions, StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        if (missingPartitions.Count != 0)
        {
            return Task.FromResult(Failure(
                request,
                entry,
                RecordSearchProjectionCoverageStatuses.Incomplete,
                RecordSearchProjectionFailureCodes.CoverageIncomplete,
                "One or more requested partitions are not covered by the selected generation.",
                retryable: true,
                requestedPartitions: requestedPartitions,
                coveredPartitions: coveredPartitions));
        }

        if (request.DeadlineUtc is { } deadline && deadline <= now)
        {
            return Task.FromResult(Failure(
                request,
                entry,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.DeadlineExceeded,
                "The generation-bound search deadline has elapsed.",
                retryable: true,
                requestedPartitions: requestedPartitions,
                coveredPartitions: coveredPartitions));
        }
        if (request.ExpectedDescriptorDigest is not null &&
            !string.Equals(request.ExpectedDescriptorDigest, entry.Descriptor.DescriptorDigest, StringComparison.Ordinal))
        {
            return Task.FromResult(Failure(
                request,
                entry,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.GenerationDescriptorMismatch,
                "The selected generation descriptor does not match the caller's expected digest.",
                retryable: false,
                requestedPartitions: requestedPartitions,
                coveredPartitions: coveredPartitions));
        }

        var unsupported = ValidateSupportedQuery(request.Query);
        if (unsupported is not null)
        {
            return Task.FromResult(Failure(
                request,
                entry,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.UnsupportedQuery,
                unsupported,
                retryable: false,
                requestedPartitions: requestedPartitions,
                coveredPartitions: coveredPartitions));
        }

        var requestFingerprint = RecordSearchProjectionGenerationContract.ComputeRequestFingerprint(
            request,
            entry.Descriptor.GenerationId,
            entry.Descriptor.DescriptorDigest,
            requestedPartitions);
        if (continuation is not null &&
            (!string.Equals(continuation.DescriptorDigest, entry.Descriptor.DescriptorDigest, StringComparison.Ordinal) ||
             !string.Equals(continuation.RequestFingerprint, requestFingerprint, StringComparison.Ordinal)))
        {
            return Task.FromResult(Failure(
                request,
                entry,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.InvalidContinuation,
                "The continuation does not match the immutable generation or request.",
                retryable: false,
                requestedPartitions: requestedPartitions,
                coveredPartitions: coveredPartitions));
        }

        var documents = entry.Documents
            .Where(document => requestedPartitions.Contains(document.Candidate.PartitionKey, StringComparer.Ordinal))
            .ToList();
        var requestedWorkLimit = request.Query.Lexical?.ScanLimit ?? _options.DefaultWorkLimit;
        var workLimit = Math.Min(requestedWorkLimit, _options.MaxWorkLimit);
        if (documents.Count > workLimit)
        {
            var failed = Failure(
                request,
                entry,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.WorkLimitExceeded,
                "The exhaustive reference work bound was exceeded; no partial candidate page was returned.",
                retryable: false,
                requestedPartitions: requestedPartitions,
                coveredPartitions: coveredPartitions);
            failed.Diagnostics.WorkLimit = workLimit;
            failed.Diagnostics.WorkUnits = documents.Count;
            failed.Diagnostics.CandidateBound = workLimit;
            RecordSearchProjectionGenerationContract.ValidateResult(failed);
            return Task.FromResult(failed);
        }

        ct.ThrowIfCancellationRequested();
        var matches = Search(documents, request.Query.Lexical, ct);
        if (request.DeadlineUtc is { } completedDeadline && completedDeadline <= UtcNow())
        {
            return Task.FromResult(Failure(
                request,
                entry,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.DeadlineExceeded,
                "The generation-bound search deadline elapsed before the bounded scan completed.",
                retryable: true,
                requestedPartitions: requestedPartitions,
                coveredPartitions: coveredPartitions));
        }
        var limit = request.Query.Limit ?? request.Query.Lexical?.Top ?? _options.DefaultResultLimit;
        if (limit <= 0 || limit > _options.MaxResultLimit ||
            request.Query.Lexical is { Top: > 0 } lexical && lexical.Top > _options.MaxResultLimit)
        {
            return Task.FromResult(Failure(
                request,
                entry,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.UnsupportedQuery,
                $"The result limit must be between 1 and {_options.MaxResultLimit}.",
                retryable: false,
                requestedPartitions: requestedPartitions,
                coveredPartitions: coveredPartitions));
        }

        var offset = continuation?.Offset ?? 0;
        if (offset < 0 || offset > matches.Count)
        {
            return Task.FromResult(Failure(
                request,
                entry,
                RecordSearchProjectionCoverageStatuses.Complete,
                RecordSearchProjectionFailureCodes.InvalidContinuation,
                "The continuation offset is outside the retained result set.",
                retryable: false,
                requestedPartitions: requestedPartitions,
                coveredPartitions: coveredPartitions));
        }
        var page = matches.Skip(offset).Take(limit).ToList();
        var nextOffset = offset + page.Count;
        var next = nextOffset < matches.Count
            ? WriteContinuation(new ContinuationPayload
            {
                Version = ContinuationVersion,
                GenerationId = entry.Descriptor.GenerationId,
                DescriptorDigest = entry.Descriptor.DescriptorDigest,
                RequestFingerprint = requestFingerprint,
                Offset = nextOffset,
                ExpiresAtUtc = now.Add(_options.ContinuationLifetime)
            })
            : null;
        var result = new GenerationBoundRecordSearchProjectionResult
        {
            Status = RecordSearchProjectionResultStatuses.Succeeded,
            GenerationId = entry.Descriptor.GenerationId,
            GenerationDescriptorDigest = entry.Descriptor.DescriptorDigest,
            Items = page,
            ContinuationToken = next,
            Coverage = new RecordSearchProjectionCoverage
            {
                Status = RecordSearchProjectionCoverageStatuses.Complete,
                RequestedPartitions = requestedPartitions,
                CoveredPartitions = coveredPartitions
            },
            Diagnostics = new RecordSearchProjectionWorkDiagnostics
            {
                WorkLimit = workLimit,
                WorkUnits = documents.Count,
                CandidateBound = workLimit,
                CandidateCount = matches.Count,
                ReturnedCount = page.Count,
                CacheStatus = RecordSearchProjectionCacheStatuses.NotApplicable
            }
        };
        RecordSearchProjectionGenerationContract.ValidateResult(result);
        return Task.FromResult(result);
    }

    private GenerationBoundRecordSearchProjectionResult Failure(
        GenerationBoundRecordSearchProjectionRequest request,
        GenerationEntry? entry,
        string coverageStatus,
        string failureCode,
        string message,
        bool retryable,
        string? generationId = null,
        List<string>? requestedPartitions = null,
        List<string>? coveredPartitions = null)
    {
        requestedPartitions ??= request.Query.PartitionKeys?
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList() ?? new List<string>();
        requestedPartitions = requestedPartitions.ToList();
        coveredPartitions = coveredPartitions?.ToList() ?? new List<string>();
        if (coverageStatus == RecordSearchProjectionCoverageStatuses.Unavailable)
        {
            coveredPartitions.Clear();
        }
        var missing = requestedPartitions
            .Except(coveredPartitions, StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var result = new GenerationBoundRecordSearchProjectionResult
        {
            Status = RecordSearchProjectionResultStatuses.Failed,
            GenerationId = entry?.Descriptor.GenerationId ?? generationId,
            GenerationDescriptorDigest = entry?.Descriptor.DescriptorDigest,
            Coverage = new RecordSearchProjectionCoverage
            {
                Status = coverageStatus,
                RequestedPartitions = requestedPartitions,
                CoveredPartitions = coveredPartitions,
                MissingPartitions = missing
            },
            Diagnostics = new RecordSearchProjectionWorkDiagnostics(),
            Failure = new RecordSearchProjectionFailure
            {
                Code = failureCode,
                Message = message,
                Retryable = retryable
            }
        };
        RecordSearchProjectionGenerationContract.ValidateResult(result);
        return result;
    }

    private static List<RecordSearchProjectionCandidate> Search(
        IReadOnlyCollection<LocalRecordSearchProjectionDocument> documents,
        LexicalSearchOptions? lexical,
        CancellationToken ct)
    {
        var queryTokens = Tokenize(lexical?.Query ?? string.Empty);
        var requireAll = lexical?.MatchMode == LexicalMatchModes.All;
        var matches = new List<RecordSearchProjectionCandidate>();
        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            var textTokens = Tokenize(document.SearchText);
            var counts = textTokens
                .GroupBy(value => value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var matched = queryTokens.Count == 0
                ? 1
                : queryTokens.Sum(token => counts.GetValueOrDefault(token));
            var accepted = queryTokens.Count == 0 ||
                (requireAll
                    ? queryTokens.All(token => counts.ContainsKey(token))
                    : matched > 0);
            if (!accepted)
            {
                continue;
            }
            matches.Add(new RecordSearchProjectionCandidate
            {
                PartitionKey = document.Candidate.PartitionKey,
                Id = document.Candidate.Id,
                Revision = document.Candidate.Revision,
                Score = matched
            });
        }
        return matches
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.PartitionKey, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                current.Append(char.ToLowerInvariant(character));
                continue;
            }
            FlushToken(current, tokens);
        }
        FlushToken(current, tokens);
        return tokens;
    }

    private static void FlushToken(StringBuilder current, List<string> tokens)
    {
        if (current.Length == 0)
        {
            return;
        }
        tokens.Add(current.ToString());
        current.Clear();
    }

    private static string? ValidateSupportedQuery(QueryEnvelope query)
    {
        if (query.Filter is not null || query.Vector is not null || query.OrderBy is { Count: > 0 })
        {
            return "The exhaustive local reference currently supports partition and lexical selection only.";
        }
        if (query.Lexical is { } lexical)
        {
            if (lexical.ScanLimit <= 0)
            {
                return "lexical.scanLimit must be greater than zero.";
            }
            if (lexical.MatchMode is not LexicalMatchModes.Any and not LexicalMatchModes.All)
            {
                return "The exhaustive local reference supports lexical matchMode any or all.";
            }
            if (lexical.Fields is { Count: > 0 } || lexical.RequiredPhraseGroups is { Count: > 0 } ||
                lexical.PrefixMatching || lexical.FieldBoosts is { Count: > 0 } || lexical.MinScore.HasValue)
            {
                return "Fielded, phrase, prefix, boosted, and score-threshold lexical behavior is capability-gated outside this reference selector.";
            }
            if (!string.Equals(lexical.Scoring, LexicalScorings.Bm25, StringComparison.Ordinal) ||
                lexical.Bm25K1 != 1.2f || lexical.Bm25B != 0.75f || lexical.PhraseBoost != 0.15f ||
                lexical.ExactBoost != 0.25f || lexical.MetadataBoost != 0.10f)
            {
                return "The exhaustive local reference does not accept caller-selected scoring parameters; scores are defined by the generation strategy.";
            }
            if (lexical.Top <= 0)
            {
                return "lexical.top must be greater than zero.";
            }
        }
        return null;
    }

    private static List<string> ResolveRequestedPartitions(
        QueryEnvelope query,
        RecordSearchProjectionGenerationDescriptor descriptor) =>
        (query.PartitionKeys is { Count: > 0 } ? query.PartitionKeys : descriptor.ExpectedPartitions)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

    private (string Code, string Message)? ReadContinuation(
        string token,
        DateTime now,
        out ContinuationPayload? payload)
    {
        payload = null;
        if (token.Length > _options.MaxContinuationTokenChars)
        {
            return (RecordSearchProjectionFailureCodes.InvalidContinuation, "The continuation exceeds the configured size bound.");
        }
        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 2 || !TryDecode(parts[0], out var body) || !TryDecode(parts[1], out var signature))
        {
            return (RecordSearchProjectionFailureCodes.InvalidContinuation, "The continuation is malformed.");
        }
        var expected = HMACSHA256.HashData(_signingKey, body);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            return (RecordSearchProjectionFailureCodes.InvalidContinuation, "The continuation signature is invalid.");
        }
        try
        {
            payload = JsonSerializer.Deserialize<ContinuationPayload>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return (RecordSearchProjectionFailureCodes.InvalidContinuation, "The continuation payload is invalid.");
        }
        if (payload is null || payload.Version != ContinuationVersion || payload.Offset < 0 ||
            string.IsNullOrWhiteSpace(payload.GenerationId) || string.IsNullOrWhiteSpace(payload.DescriptorDigest) ||
            string.IsNullOrWhiteSpace(payload.RequestFingerprint))
        {
            payload = null;
            return (RecordSearchProjectionFailureCodes.InvalidContinuation, "The continuation payload is invalid.");
        }
        if (payload.ExpiresAtUtc <= now)
        {
            return (RecordSearchProjectionFailureCodes.ExpiredContinuation, "The continuation has expired.");
        }
        return null;
    }

    private string WriteContinuation(ContinuationPayload payload)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var signature = HMACSHA256.HashData(_signingKey, body);
        return Encode(body) + "." + Encode(signature);
    }

    private static string Encode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string value, out byte[] bytes)
    {
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private GenerationEntry? GetEntry(string collection, string generationId) =>
        _collections.TryGetValue(collection, out var generations) && generations.TryGetValue(generationId, out var entry)
            ? entry
            : null;

    private DateTime UtcNow() => _options.TimeProvider.GetUtcNow().UtcDateTime;

    private static void ValidateDocuments(LocalRecordSearchProjectionGeneration generation)
    {
        if (generation.Documents.Count != generation.Descriptor.ExpectedItemCount)
        {
            throw new InvalidOperationException("Generation document count does not match descriptor expectedItemCount.");
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in generation.Documents)
        {
            var candidate = document.Candidate ?? throw new InvalidOperationException("A local generation document requires a candidate identity.");
            if (!generation.Descriptor.ExpectedPartitions.Contains(candidate.PartitionKey, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("A generation document partition is absent from descriptor expectedPartitions.");
            }
            if (string.IsNullOrWhiteSpace(candidate.Id) || candidate.Id.Length > 200 ||
                !string.Equals(candidate.Id, candidate.Id.Trim(), StringComparison.Ordinal) ||
                candidate.Id.Any(char.IsControl) || candidate.Revision <= 0 || !float.IsFinite(candidate.Score))
            {
                throw new InvalidOperationException("A generation document requires a non-empty ID, positive revision, and finite score.");
            }
            if (!identities.Add(candidate.PartitionKey + "\n" + candidate.Id))
            {
                throw new InvalidOperationException("Generation documents must have unique partition/id identities.");
            }
            if (document.SearchText is null)
            {
                throw new InvalidOperationException("A local generation document requires search text.");
            }
        }
    }

    private static RecordSearchProjectionGenerationDescriptor CloneDescriptor(
        RecordSearchProjectionGenerationDescriptor descriptor) =>
        JsonSerializer.Deserialize<RecordSearchProjectionGenerationDescriptor>(
            JsonSerializer.Serialize(descriptor, JsonOptions),
            JsonOptions) ?? throw new InvalidOperationException("The generation descriptor could not be cloned.");

    private static LocalRecordSearchProjectionDocument CloneDocument(LocalRecordSearchProjectionDocument document) => new()
    {
        Candidate = new RecordSearchProjectionCandidate
        {
            PartitionKey = document.Candidate.PartitionKey,
            Id = document.Candidate.Id,
            Revision = document.Candidate.Revision,
            Score = document.Candidate.Score
        },
        SearchText = document.SearchText
    };

    private static bool GenerationContentEquals(GenerationEntry left, GenerationEntry right) =>
        left.AvailablePartitions.SequenceEqual(right.AvailablePartitions, StringComparer.Ordinal) &&
        left.Documents.Count == right.Documents.Count &&
        OrderDocuments(left.Documents).Zip(OrderDocuments(right.Documents)).All(pair =>
            string.Equals(pair.First.Candidate.PartitionKey, pair.Second.Candidate.PartitionKey, StringComparison.Ordinal) &&
            string.Equals(pair.First.Candidate.Id, pair.Second.Candidate.Id, StringComparison.Ordinal) &&
            pair.First.Candidate.Revision == pair.Second.Candidate.Revision &&
            pair.First.Candidate.Score.Equals(pair.Second.Candidate.Score) &&
            string.Equals(pair.First.SearchText, pair.Second.SearchText, StringComparison.Ordinal));

    private static IEnumerable<LocalRecordSearchProjectionDocument> OrderDocuments(
        IEnumerable<LocalRecordSearchProjectionDocument> documents) =>
        documents
            .OrderBy(document => document.Candidate.PartitionKey, StringComparer.Ordinal)
            .ThenBy(document => document.Candidate.Id, StringComparer.Ordinal);

    private sealed class GenerationEntry
    {
        public GenerationEntry(
            RecordSearchProjectionGenerationDescriptor descriptor,
            List<LocalRecordSearchProjectionDocument> documents,
            List<string> availablePartitions,
            string state)
        {
            Descriptor = descriptor;
            Documents = documents;
            AvailablePartitions = availablePartitions;
            State = state;
        }

        public RecordSearchProjectionGenerationDescriptor Descriptor { get; }
        public List<LocalRecordSearchProjectionDocument> Documents { get; }
        public List<string> AvailablePartitions { get; set; }
        public string State { get; set; }

        public GenerationEntry Clone() => new(
            CloneDescriptor(Descriptor),
            Documents.Select(CloneDocument).ToList(),
            AvailablePartitions.ToList(),
            State);
    }

    private sealed class ContinuationPayload
    {
        public string Version { get; set; } = string.Empty;
        public string GenerationId { get; set; } = string.Empty;
        public string DescriptorDigest { get; set; } = string.Empty;
        public string RequestFingerprint { get; set; } = string.Empty;
        public int Offset { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
    }
}
