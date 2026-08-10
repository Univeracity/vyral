using System;
using System.Collections.Generic;

namespace Vyral.Bridge.SemanticKernel;

public static class VyralSemanticKernelAlignment
{
    public static SemanticKernelAlignmentProfile Current { get; } = new()
    {
        BridgeVersion = "0.1",
        CoreContract = "Vyral RecordCollectionStore",
        TargetContract = "Microsoft.Extensions.VectorData.VectorStore",
        Mappings = new[]
        {
            new SemanticKernelAlignmentMapping(
                "record collection",
                "VectorStoreCollection<TKey, TRecord>",
                SemanticKernelAlignmentStatuses.Aligned,
                "A Semantic Kernel collection name maps directly to a Vyral record collection name."),
            new SemanticKernelAlignmentMapping(
                "record id and partition key",
                "TKey",
                SemanticKernelAlignmentStatuses.CallerMapped,
                "The caller supplies stable key, id, and partition-key delegates because Vyral keeps partitioning explicit."),
            new SemanticKernelAlignmentMapping(
                "typed record",
                "TRecord",
                SemanticKernelAlignmentStatuses.CallerMapped,
                "The caller owns conversion between its typed record and the Vyral JSON record envelope."),
            new SemanticKernelAlignmentMapping(
                "metadata and content",
                "data/filter properties",
                SemanticKernelAlignmentStatuses.CallerMapped,
                "Filterable Semantic Kernel properties map to Vyral JSON pointer paths in metadata, content, or record identity fields."),
            new SemanticKernelAlignmentMapping(
                "vector field",
                "vector property",
                SemanticKernelAlignmentStatuses.Aligned,
                "A configured Vyral vector field maps to the collection's active Semantic Kernel vector search property."),
            new SemanticKernelAlignmentMapping(
                "RecordCollectionPolicy.VectorPolicies",
                "vector dimensions, datatype, distance function, and index shape",
                SemanticKernelAlignmentStatuses.Aligned,
                "Vyral collection policy remains the source of truth for vector path and provider-profile constraints."),
            new SemanticKernelAlignmentMapping(
                "QueryEnvelope filters and ordering",
                "filtered retrieval/search predicates",
                SemanticKernelAlignmentStatuses.Partial,
                "Simple comparisons, in, string contains, string startsWith, and mapped ordering translate into Vyral filters."),
            new SemanticKernelAlignmentMapping(
                "RAG chunks and manifests",
                "records with application-owned shape",
                SemanticKernelAlignmentStatuses.CallerMapped,
                "RAG-specific records stay normal Vyral records; Semantic Kernel does not become a separate RAG ontology.")
        },
        SupportedFeatures = new[]
        {
            "typed mapped collection creation",
            "typed upsert/get/delete",
            "typed filtered retrieval",
            "typed vector search",
            "simple predicate filter translation",
            "mapped ordering",
            "partition-aware key resolution",
            "collection policy creation through Vyral"
        },
        DeferredFeatures = new[]
        {
            "dynamic Semantic Kernel collections",
            "automatic reflection-based record schema mapping",
            "multiple vector property selection per call",
            "provider-native index creation outside Vyral collection policy",
            "full Semantic Kernel memory abstractions",
            "Semantic Kernel ownership of RAG context assembly"
        },
        ConformanceTargets = new[]
        {
            "A mapped SK collection can round-trip records through Vyral without bypassing partition keys.",
            "SK vector search compiles to Vyral QueryEnvelope vector search over the configured vector field.",
            "SK filters compile only when each referenced property has an explicit Vyral JSON pointer mapping or is record identity.",
            "Vyral policy validation remains responsible for provider-profile vector constraints before migration rehearsal.",
            "RAG context assembly remains Vyral-owned and can consume records written through the SK bridge."
        }
    };
}

public sealed class SemanticKernelAlignmentProfile
{
    public string BridgeVersion { get; init; } = string.Empty;

    public string CoreContract { get; init; } = string.Empty;

    public string TargetContract { get; init; } = string.Empty;

    public IReadOnlyList<SemanticKernelAlignmentMapping> Mappings { get; init; } = Array.Empty<SemanticKernelAlignmentMapping>();

    public IReadOnlyList<string> SupportedFeatures { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DeferredFeatures { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ConformanceTargets { get; init; } = Array.Empty<string>();
}

public sealed record SemanticKernelAlignmentMapping(
    string VyralConcept,
    string SemanticKernelConcept,
    string Status,
    string Notes);

public static class SemanticKernelAlignmentStatuses
{
    public const string Aligned = "aligned";
    public const string CallerMapped = "caller-mapped";
    public const string Partial = "partial";
    public const string Deferred = "deferred";
}
