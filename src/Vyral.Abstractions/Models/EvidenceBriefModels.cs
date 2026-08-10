using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Vyral.Abstractions.Models;

/// <summary>
/// Versioned, application-neutral evidence artifact. An EvidenceBrief records the evidence used
/// for a dated question; it deliberately does not contain a generated answer, persona prompt, or
/// truth-adjudication decision.
/// </summary>
public sealed class EvidenceBrief
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = EvidenceBriefContract.SchemaV1;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("question")]
    public string Question { get; set; } = string.Empty;

    [JsonPropertyName("asOfUtc")]
    public DateTime AsOfUtc { get; set; }

    [JsonPropertyName("factAnchors")]
    public List<EvidenceBriefFactAnchor> FactAnchors { get; set; } = new();

    [JsonPropertyName("sourceSnapshots")]
    public List<EvidenceBriefSourceSnapshot> SourceSnapshots { get; set; } = new();

    [JsonPropertyName("citations")]
    public List<EvidenceBriefCitation> Citations { get; set; } = new();

    /// <summary>Known material that qualifies or conflicts with the fact anchors. An empty list is an explicit declaration that none was recorded.</summary>
    [JsonPropertyName("counterEvidence")]
    public List<EvidenceBriefCounterEvidence> CounterEvidence { get; set; } = new();

    /// <summary>Known uncertainty, scope limits, or unanswered questions. This is not a numerical truth score.</summary>
    [JsonPropertyName("uncertainties")]
    public List<EvidenceBriefUncertainty> Uncertainties { get; set; } = new();

    /// <summary>Safe references to the retrieval work that selected the evidence. Raw prompts, tokens, and credentials do not belong here.</summary>
    [JsonPropertyName("retrievalTraces")]
    public List<EvidenceBriefRetrievalTrace> RetrievalTraces { get; set; } = new();
}

public sealed class EvidenceBriefFactAnchor
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("statement")]
    public string Statement { get; set; } = string.Empty;

    [JsonPropertyName("sourceSnapshotIds")]
    public List<string> SourceSnapshotIds { get; set; } = new();

    [JsonPropertyName("citationIds")]
    public List<string> CitationIds { get; set; } = new();
}

public sealed class EvidenceBriefSourceSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Consumer-defined source kind, for example <c>web</c>, <c>record</c>, or <c>object</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Stable, credential-free source locator. Query strings and fragments are excluded so they cannot carry signed URLs or unstable citation anchors.</summary>
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("capturedAtUtc")]
    public DateTime CapturedAtUtc { get; set; }

    [JsonPropertyName("publishedAtUtc")]
    public DateTime? PublishedAtUtc { get; set; }
}

/// <summary>A presentation-ready citation bound to an immutable source snapshot, rather than an unversioned latest URL.</summary>
public sealed class EvidenceBriefCitation
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("sourceSnapshotId")]
    public string SourceSnapshotId { get; set; } = string.Empty;

    [JsonPropertyName("factAnchorIds")]
    public List<string> FactAnchorIds { get; set; } = new();

    [JsonPropertyName("counterEvidenceIds")]
    public List<string> CounterEvidenceIds { get; set; } = new();

    [JsonPropertyName("displayText")]
    public string DisplayText { get; set; } = string.Empty;

    /// <summary>Optional source-local presentation anchor such as a page, section, or quote label.</summary>
    [JsonPropertyName("locator")]
    public string? Locator { get; set; }
}

public sealed class EvidenceBriefCounterEvidence
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("statement")]
    public string Statement { get; set; } = string.Empty;

    [JsonPropertyName("sourceSnapshotIds")]
    public List<string> SourceSnapshotIds { get; set; } = new();

    [JsonPropertyName("citationIds")]
    public List<string> CitationIds { get; set; } = new();
}

public static class EvidenceBriefUncertaintyLevels
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Unknown = "unknown";
}

public sealed class EvidenceBriefUncertainty
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("statement")]
    public string Statement { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public string Level { get; set; } = EvidenceBriefUncertaintyLevels.Unknown;

    [JsonPropertyName("factAnchorIds")]
    public List<string> FactAnchorIds { get; set; } = new();
}

public sealed class EvidenceBriefRetrievalTrace
{
    /// <summary>Trace id emitted by Vyral or a consumer-owned retrieval system.</summary>
    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = string.Empty;

    [JsonPropertyName("retrievedAtUtc")]
    public DateTime RetrievedAtUtc { get; set; }

    /// <summary>SHA-256 digest of the normalized retrieval request. The brief retains its human question separately but does not duplicate an arbitrary raw request payload.</summary>
    [JsonPropertyName("queryHash")]
    public string QueryHash { get; set; } = string.Empty;

    [JsonPropertyName("matches")]
    public List<EvidenceBriefRetrievalMatch> Matches { get; set; } = new();
}

public sealed class EvidenceBriefRetrievalMatch
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("recordId")]
    public string RecordId { get; set; } = string.Empty;

    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("sourceSnapshotIds")]
    public List<string> SourceSnapshotIds { get; set; } = new();
}

/// <summary>Input for an atomic EvidenceBrief canonical write. The optional outbox event is a projection wake-up, not evidence completeness proof.</summary>
public sealed class EvidenceBriefWriteRequest
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [JsonPropertyName("brief")]
    public EvidenceBrief Brief { get; set; } = new();

    [JsonPropertyName("expectedRevision")]
    public long? ExpectedRevision { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("actor")]
    public string? Actor { get; set; }

    [JsonPropertyName("emitChangeEvent")]
    public bool EmitChangeEvent { get; set; } = true;

    [JsonPropertyName("changeEventTopic")]
    public string ChangeEventTopic { get; set; } = EvidenceBriefContract.DefaultChangedEventTopic;
}

/// <summary>Typed view of the canonical document carrying an EvidenceBrief.</summary>
public sealed class EvidenceBriefDocument
{
    public CanonicalDocument Document { get; init; } = new();
    public EvidenceBrief Brief { get; init; } = new();
}

public static class EvidenceBriefContract
{
    public const string SchemaV1 = "vyral.evidence-brief.v1";
    public const string CanonicalDocumentType = "vyral.evidence-brief";
    public const string DefaultChangedEventTopic = "vyral.evidence-brief.changed";
    public const string JsonSchemaResourcePath = "contracts/evidence-brief.v1.schema.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ValidUncertaintyLevels = new(StringComparer.Ordinal)
    {
        EvidenceBriefUncertaintyLevels.Low,
        EvidenceBriefUncertaintyLevels.Medium,
        EvidenceBriefUncertaintyLevels.High,
        EvidenceBriefUncertaintyLevels.Unknown
    };

    public static CanonicalTransactionRequest CreateUpsertTransaction(EvidenceBriefWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateWriteRequest(request);

        var transaction = new CanonicalTransactionRequest
        {
            TenantId = request.TenantId.Trim(),
            IdempotencyKey = request.IdempotencyKey.Trim(),
            CorrelationId = NormalizeOptional(request.CorrelationId),
            Actor = NormalizeOptional(request.Actor),
            Mutations =
            [
                new CanonicalMutation
                {
                    Operation = CanonicalMutationOperations.Upsert,
                    Document = ToCanonicalDocument(request.TenantId, request.Brief),
                    Precondition = request.ExpectedRevision.HasValue
                        ? new CanonicalWritePrecondition { ExpectedRevision = request.ExpectedRevision }
                        : null
                }
            ]
        };

        if (request.EmitChangeEvent)
        {
            transaction.Outbox.Add(new CanonicalOutboxWrite
            {
                Topic = request.ChangeEventTopic.Trim(),
                Key = request.Brief.Id.Trim(),
                Payload = new JsonObject
                {
                    ["briefId"] = request.Brief.Id.Trim(),
                    ["schema"] = SchemaV1,
                    ["asOfUtc"] = request.Brief.AsOfUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                }
            });
        }

        CanonicalContractValidator.ValidateTransaction(transaction);
        return transaction;
    }

    public static CanonicalDocument ToCanonicalDocument(string tenantId, EvidenceBrief brief)
    {
        ValidateTenantId(tenantId);
        Validate(brief);
        return new CanonicalDocument
        {
            TenantId = tenantId.Trim(),
            DocumentType = CanonicalDocumentType,
            Id = brief.Id.Trim(),
            SchemaVersion = SchemaV1,
            Data = JsonSerializer.SerializeToNode(brief, SerializerOptions),
            Indexes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["schema"] = SchemaV1,
                ["asOfUtc"] = brief.AsOfUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            }
        };
    }

    public static EvidenceBriefDocument FromCanonicalDocument(CanonicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(document.DocumentType, CanonicalDocumentType, StringComparison.Ordinal))
            throw new InvalidOperationException($"Canonical document type must be '{CanonicalDocumentType}'.");
        if (!string.Equals(document.SchemaVersion, SchemaV1, StringComparison.Ordinal))
            throw new InvalidOperationException($"Canonical EvidenceBrief schema version must be '{SchemaV1}'.");
        var brief = document.Data?.Deserialize<EvidenceBrief>(SerializerOptions)
            ?? throw new InvalidOperationException("Canonical EvidenceBrief document data is required.");
        if (!string.Equals(document.Id, brief.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical EvidenceBrief document id must match data.id.");
        Validate(brief);
        return new EvidenceBriefDocument { Document = document, Brief = brief };
    }

    public static void ValidateWriteRequest(EvidenceBriefWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTenantId(request.TenantId);
        ValidateId(request.IdempotencyKey, "EvidenceBrief idempotency key");
        if (request.ExpectedRevision < 0) throw new InvalidOperationException("EvidenceBrief expectedRevision cannot be negative.");
        if (!string.IsNullOrWhiteSpace(request.CorrelationId)) ValidateId(request.CorrelationId, "EvidenceBrief correlation id");
        if (!string.IsNullOrWhiteSpace(request.Actor)) ValidateId(request.Actor, "EvidenceBrief actor");
        if (request.EmitChangeEvent) ValidateId(request.ChangeEventTopic, "EvidenceBrief change event topic");
        Validate(request.Brief);
    }

    public static void Validate(EvidenceBrief brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        if (!string.Equals(brief.Schema, SchemaV1, StringComparison.Ordinal))
            throw new InvalidOperationException($"EvidenceBrief schema must be '{SchemaV1}'.");
        ValidateId(brief.Id, "EvidenceBrief id");
        ValidateText(brief.Question, "EvidenceBrief question", 16_384);
        ValidateUtc(brief.AsOfUtc, "EvidenceBrief asOfUtc");
        ValidateCollectionSize(brief.FactAnchors, "EvidenceBrief factAnchors", 1, 256);
        ValidateCollectionSize(brief.SourceSnapshots, "EvidenceBrief sourceSnapshots", 1, 512);
        ValidateCollectionSize(brief.Citations, "EvidenceBrief citations", 1, 512);
        ValidateCollectionSize(brief.CounterEvidence, "EvidenceBrief counterEvidence", 0, 256);
        ValidateCollectionSize(brief.Uncertainties, "EvidenceBrief uncertainties", 0, 256);
        ValidateCollectionSize(brief.RetrievalTraces, "EvidenceBrief retrievalTraces", 1, 256);

        var sources = IndexById(brief.SourceSnapshots, item => item.Id, "EvidenceBrief source snapshot");
        foreach (var source in brief.SourceSnapshots)
        {
            ValidateId(source.Id, "EvidenceBrief source snapshot id");
            ValidateId(source.Kind, "EvidenceBrief source snapshot kind");
            ValidateSourceUri(source.Uri);
            ValidateSha256(source.ContentHash, "EvidenceBrief source snapshot contentHash");
            ValidateUtc(source.CapturedAtUtc, "EvidenceBrief source snapshot capturedAtUtc");
            if (source.PublishedAtUtc.HasValue) ValidateUtc(source.PublishedAtUtc.Value, "EvidenceBrief source snapshot publishedAtUtc");
        }

        var anchors = IndexById(brief.FactAnchors, item => item.Id, "EvidenceBrief fact anchor");
        foreach (var anchor in brief.FactAnchors)
        {
            ValidateId(anchor.Id, "EvidenceBrief fact anchor id");
            ValidateText(anchor.Statement, "EvidenceBrief fact anchor statement", 16_384);
            ValidateReferences(anchor.SourceSnapshotIds, sources, "EvidenceBrief fact anchor sourceSnapshotIds", minItems: 1);
            ValidateNonEmptyDistinct(anchor.CitationIds, "EvidenceBrief fact anchor citationIds", 512);
        }

        var counterEvidence = IndexById(brief.CounterEvidence, item => item.Id, "EvidenceBrief counter evidence");
        foreach (var item in brief.CounterEvidence)
        {
            ValidateId(item.Id, "EvidenceBrief counter evidence id");
            ValidateText(item.Statement, "EvidenceBrief counter evidence statement", 16_384);
            ValidateReferences(item.SourceSnapshotIds, sources, "EvidenceBrief counter evidence sourceSnapshotIds", minItems: 1);
            ValidateNonEmptyDistinct(item.CitationIds, "EvidenceBrief counter evidence citationIds", 512);
        }

        var citations = IndexById(brief.Citations, item => item.Id, "EvidenceBrief citation");
        foreach (var citation in brief.Citations)
        {
            ValidateId(citation.Id, "EvidenceBrief citation id");
            ValidateReference(citation.SourceSnapshotId, sources, "EvidenceBrief citation sourceSnapshotId");
            if (citation.FactAnchorIds.Count == 0 && citation.CounterEvidenceIds.Count == 0)
                throw new InvalidOperationException("EvidenceBrief citation must reference a fact anchor or counter evidence item.");
            ValidateReferences(citation.FactAnchorIds, anchors, "EvidenceBrief citation factAnchorIds");
            ValidateReferences(citation.CounterEvidenceIds, counterEvidence, "EvidenceBrief citation counterEvidenceIds");
            ValidateText(citation.DisplayText, "EvidenceBrief citation displayText", 4_096);
            if (!string.IsNullOrWhiteSpace(citation.Locator)) ValidateText(citation.Locator, "EvidenceBrief citation locator", 1_024);
        }

        foreach (var anchor in brief.FactAnchors)
        {
            foreach (var citationId in anchor.CitationIds)
            {
                ValidateReference(citationId, citations, "EvidenceBrief fact anchor citationIds");
                var citation = citations[citationId];
                if (!citation.FactAnchorIds.Contains(anchor.Id, StringComparer.Ordinal))
                    throw new InvalidOperationException($"EvidenceBrief citation '{citationId}' must list fact anchor '{anchor.Id}'.");
                if (!anchor.SourceSnapshotIds.Contains(citation.SourceSnapshotId, StringComparer.Ordinal))
                    throw new InvalidOperationException($"EvidenceBrief citation '{citationId}' source must be listed by fact anchor '{anchor.Id}'.");
            }
        }

        foreach (var item in brief.CounterEvidence)
        {
            foreach (var citationId in item.CitationIds)
            {
                ValidateReference(citationId, citations, "EvidenceBrief counter evidence citationIds");
                var citation = citations[citationId];
                if (!citation.CounterEvidenceIds.Contains(item.Id, StringComparer.Ordinal))
                    throw new InvalidOperationException($"EvidenceBrief citation '{citationId}' must list counter evidence '{item.Id}'.");
                if (!item.SourceSnapshotIds.Contains(citation.SourceSnapshotId, StringComparer.Ordinal))
                    throw new InvalidOperationException($"EvidenceBrief citation '{citationId}' source must be listed by counter evidence '{item.Id}'.");
            }
        }

        var uncertainties = IndexById(brief.Uncertainties, item => item.Id, "EvidenceBrief uncertainty");
        foreach (var uncertainty in brief.Uncertainties)
        {
            ValidateId(uncertainty.Id, "EvidenceBrief uncertainty id");
            ValidateText(uncertainty.Statement, "EvidenceBrief uncertainty statement", 16_384);
            if (!ValidUncertaintyLevels.Contains(uncertainty.Level))
                throw new InvalidOperationException($"EvidenceBrief uncertainty level '{uncertainty.Level}' is not supported.");
            ValidateReferences(uncertainty.FactAnchorIds, anchors, "EvidenceBrief uncertainty factAnchorIds", minItems: 1);
        }

        var traceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trace in brief.RetrievalTraces)
        {
            ValidateId(trace.TraceId, "EvidenceBrief retrieval trace id");
            if (!traceIds.Add(trace.TraceId)) throw new InvalidOperationException($"EvidenceBrief duplicates retrieval trace '{trace.TraceId}'.");
            ValidateUtc(trace.RetrievedAtUtc, "EvidenceBrief retrieval trace retrievedAtUtc");
            ValidateSha256(trace.QueryHash, "EvidenceBrief retrieval trace queryHash");
            ValidateCollectionSize(trace.Matches, "EvidenceBrief retrieval trace matches", 1, 1_000);
            var ranks = new HashSet<int>();
            foreach (var match in trace.Matches)
            {
                ValidateId(match.Collection, "EvidenceBrief retrieval match collection");
                ValidateId(match.RecordId, "EvidenceBrief retrieval match recordId");
                if (match.Rank <= 0 || !ranks.Add(match.Rank)) throw new InvalidOperationException("EvidenceBrief retrieval match ranks must be unique positive integers within a trace.");
                ValidateReferences(match.SourceSnapshotIds, sources, "EvidenceBrief retrieval match sourceSnapshotIds", minItems: 1);
            }
        }

        _ = uncertainties;
    }

    private static void ValidateTenantId(string value)
    {
        try
        {
            CanonicalContractValidator.ValidateTenantId(value.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"EvidenceBrief tenantId is invalid: {exception.Message}", exception);
        }
    }

    private static void ValidateId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required.");
        if (value.Trim().Length > 1_024) throw new InvalidOperationException($"{name} cannot exceed 1024 characters.");
        if (value.Any(char.IsControl)) throw new InvalidOperationException($"{name} cannot contain control characters.");
    }

    private static void ValidateText(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required.");
        if (value.Length > maxLength) throw new InvalidOperationException($"{name} cannot exceed {maxLength} characters.");
        if (value.Any(char.IsControl) && value.Any(character => character is '\u0000' or '\r'))
            throw new InvalidOperationException($"{name} cannot contain NUL or carriage-return characters.");
    }

    private static void ValidateUtc(DateTime value, string name)
    {
        if (value == default) throw new InvalidOperationException($"{name} is required.");
        if (value.Kind != DateTimeKind.Utc) throw new InvalidOperationException($"{name} must be UTC.");
    }

    private static void ValidateSourceUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4_096) throw new InvalidOperationException("EvidenceBrief source snapshot uri is required and must be at most 4096 characters.");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new InvalidOperationException("EvidenceBrief source snapshot uri must be an absolute URI.");
        if (uri.Scheme is not ("http" or "https" or "urn" or "vyral"))
            throw new InvalidOperationException("EvidenceBrief source snapshot uri scheme must be http, https, urn, or vyral.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("EvidenceBrief source snapshot uri cannot contain credentials, query parameters, or fragments.");
    }

    private static void ValidateSha256(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, "\\Asha256:[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException($"{name} must be a sha256: digest.");
    }

    private static Dictionary<string, T> IndexById<T>(IEnumerable<T> items, Func<T, string> selector, string name)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var id = selector(item);
            ValidateId(id, $"{name} id");
            if (!result.TryAdd(id, item)) throw new InvalidOperationException($"EvidenceBrief duplicates {name} '{id}'.");
        }
        return result;
    }

    private static void ValidateReferences<T>(IReadOnlyList<string> references, IReadOnlyDictionary<string, T> known, string name, int minItems = 0)
    {
        if (references.Count < minItems) throw new InvalidOperationException($"{name} requires at least {minItems} item(s).");
        ValidateNonEmptyDistinct(references, name, 512, allowEmpty: minItems == 0);
        foreach (var reference in references) ValidateReference(reference, known, name);
    }

    private static void ValidateReference<T>(string reference, IReadOnlyDictionary<string, T> known, string name)
    {
        ValidateId(reference, name);
        if (!known.ContainsKey(reference)) throw new InvalidOperationException($"{name} references unknown id '{reference}'.");
    }

    private static void ValidateNonEmptyDistinct(IReadOnlyList<string> values, string name, int maxItems, bool allowEmpty = false)
    {
        if (!allowEmpty && values.Count == 0) throw new InvalidOperationException($"{name} is required.");
        if (values.Count > maxItems) throw new InvalidOperationException($"{name} cannot contain more than {maxItems} items.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ValidateId(value, name);
            if (!seen.Add(value)) throw new InvalidOperationException($"{name} cannot contain duplicate id '{value}'.");
        }
    }

    private static void ValidateCollectionSize<T>(IReadOnlyCollection<T> values, string name, int minItems, int maxItems)
    {
        if (values.Count < minItems || values.Count > maxItems)
            throw new InvalidOperationException($"{name} must contain between {minItems} and {maxItems} items.");
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
