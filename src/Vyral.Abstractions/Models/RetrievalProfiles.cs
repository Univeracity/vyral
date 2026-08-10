using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Vyral.Abstractions.Interfaces;

namespace Vyral.Abstractions.Models;

public static class RetrievalProfileIds
{
    public const string Evidence = "evidence";
    public const string RagBaseline = "ragBaseline";
    public const string RerankPolish = "rerankPolish";
    public const string DeepQuality = "deepQuality";
    public const string Discovery = "discovery";
    public const string ProductOptimization = "productOptimization";
}

public class RetrievalProfileDescriptor
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("searchMode")]
    public string SearchMode { get; set; } = SearchModes.Lexical;

    [JsonPropertyName("requiresVector")]
    public bool RequiresVector { get; set; }

    [JsonPropertyName("usesRerank")]
    public bool UsesRerank { get; set; }

    [JsonPropertyName("recommendedFor")]
    public List<string> RecommendedFor { get; set; } = new();

    [JsonPropertyName("cautions")]
    public List<string> Cautions { get; set; } = new();

    [JsonPropertyName("defaults")]
    public RetrievalProfileDefaults Defaults { get; set; } = new();
}

public class RetrievalProfileDefaults
{
    [JsonPropertyName("searchMode")]
    public string SearchMode { get; set; } = SearchModes.Lexical;

    [JsonPropertyName("embedding")]
    public EmbeddingOptions? Embedding { get; set; }

    [JsonPropertyName("lexical")]
    public LexicalSearchOptions? Lexical { get; set; }

    [JsonPropertyName("hybrid")]
    public HybridSearchOptions? Hybrid { get; set; }

    [JsonPropertyName("rerank")]
    public RerankOptions? Rerank { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 10;

    [JsonPropertyName("includeTrace")]
    public bool IncludeTrace { get; set; } = true;
}

public static class RetrievalProfileCatalog
{
    public const int RetrievalRequestDefaultLimit = 10;

    public static IReadOnlyList<string> VerifiedEvidenceFields { get; } = new[]
    {
        "/content/text",
        "/metadata/referenceId",
        "/metadata/page",
        "/metadata/title",
        "/metadata/source",
        "/id"
    };

    public static IReadOnlyDictionary<string, float> VerifiedEvidenceFieldBoosts { get; } =
        new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["/metadata/referenceId"] = 3.0f,
            ["/id"] = 1.5f,
            ["/metadata/page"] = 1.25f,
            ["/metadata/title"] = 1.15f
        };

    public static IReadOnlyList<string> RagFields { get; } = new[]
    {
        "/content/text",
        "/metadata/title",
        "/metadata/source",
        "/id"
    };

    public static IReadOnlyList<string> ProductOptimizationFields { get; } = new[]
    {
        "/content/title",
        "/content/bullets",
        "/content/description",
        "/content/text",
        "/metadata/keywords",
        "/metadata/brand",
        "/metadata/source",
        "/id"
    };

    private static readonly IReadOnlyList<RetrievalProfileDescriptor> Profiles = BuildProfiles();

    public static IReadOnlyList<RetrievalProfileDescriptor> GetProfiles() => Profiles;

    public static RetrievalProfileDescriptor GetRequired(string profile)
    {
        var resolved = GetProfiles().FirstOrDefault(item =>
            string.Equals(item.Id, profile, StringComparison.OrdinalIgnoreCase));
        if (resolved is null)
        {
            throw new InvalidOperationException($"Retrieval profile '{profile}' is not supported.");
        }

        return resolved;
    }

    public static RetrievalRequest Apply(RetrievalRequest source)
    {
        if (string.IsNullOrWhiteSpace(source.Profile))
        {
            return source;
        }

        var profile = GetRequired(source.Profile);
        var defaults = profile.Defaults;
        var request = Clone(source);
        request.Profile = profile.Id;

        if (string.IsNullOrWhiteSpace(request.SearchMode))
        {
            request.SearchMode = defaults.SearchMode;
        }

        request.Embedding ??= CloneEmbedding(defaults.Embedding);
        request.Lexical ??= CloneLexical(defaults.Lexical);
        request.Hybrid ??= CloneHybrid(defaults.Hybrid);
        request.Rerank ??= CloneRerank(defaults.Rerank);

        if (request.Limit == RetrievalRequestDefaultLimit && defaults.Limit > 0)
        {
            request.Limit = defaults.Limit;
        }

        if (defaults.IncludeTrace)
        {
            request.IncludeTrace = true;
        }

        return request;
    }

    private static IReadOnlyList<RetrievalProfileDescriptor> BuildProfiles() => new[]
    {
        new RetrievalProfileDescriptor
        {
            Id = RetrievalProfileIds.Evidence,
            Label = "Evidence lookup",
            Description = "Lexical-first retrieval for exact identifiers, stable source references, citations, and other verified-reference workflows.",
            SearchMode = SearchModes.Lexical,
            RecommendedFor = new List<string> { "verified records", "exact identifiers", "source-reference lookup", "citation-sensitive QA" },
            Cautions = new List<string> { "Use labeled evaluation before adding vectors to verified claim retrieval." },
            Defaults = new RetrievalProfileDefaults
            {
                SearchMode = SearchModes.Lexical,
                Lexical = BuildEvidenceLexical(),
                Limit = 8
            }
        },
        new RetrievalProfileDescriptor
        {
            Id = RetrievalProfileIds.RagBaseline,
            Label = "RAG baseline",
            Description = "Stable lexical RAG baseline for local development and regression testing before semantic or rerank promotion.",
            SearchMode = SearchModes.Lexical,
            RecommendedFor = new List<string> { "RAG baseline", "deterministic local testing", "citation-markdown context assembly" },
            Cautions = new List<string> { "Semantic recall should be characterized separately with a labeled fixture." },
            Defaults = new RetrievalProfileDefaults
            {
                SearchMode = SearchModes.Lexical,
                Lexical = BuildRagLexical(),
                Limit = 8
            }
        },
        new RetrievalProfileDescriptor
        {
            Id = RetrievalProfileIds.RerankPolish,
            Label = "Rerank polish",
            Description = "Lexical retrieval with a conservative rerank pass for modest top-rank improvement without losing lexical recall.",
            SearchMode = SearchModes.Lexical,
            UsesRerank = true,
            RecommendedFor = new List<string> { "rank polishing", "small candidate rerank", "evidence retrieval experiments" },
            Cautions = new List<string> { "Requires a configured rerank provider for non-token-overlap behavior.", "Fallback diagnostics should be reviewed during evaluation." },
            Defaults = new RetrievalProfileDefaults
            {
                SearchMode = SearchModes.Lexical,
                Lexical = BuildEvidenceLexical(),
                Rerank = new RerankOptions
                {
                    Enabled = true,
                    CandidateLimit = 8,
                    MaxCandidateChars = 1000,
                    FallbackOnFailure = true
                },
                Limit = 8
            }
        },
        new RetrievalProfileDescriptor
        {
            Id = RetrievalProfileIds.DeepQuality,
            Label = "Deep quality",
            Description = "Lexical retrieval with larger rerank payloads for quality sweeps where slower mixed rerank/fallback behavior is acceptable.",
            SearchMode = SearchModes.Lexical,
            UsesRerank = true,
            RecommendedFor = new List<string> { "quality characterization", "offline evaluation", "candidate sweep testing" },
            Cautions = new List<string> { "Slower than baseline retrieval.", "Oversized rerank payloads may fall back; inspect fallback rates before promotion." },
            Defaults = new RetrievalProfileDefaults
            {
                SearchMode = SearchModes.Lexical,
                Lexical = BuildEvidenceLexical(),
                Rerank = new RerankOptions
                {
                    Enabled = true,
                    CandidateLimit = 40,
                    MaxCandidateChars = 2000,
                    FallbackOnFailure = true
                },
                Limit = 8
            }
        },
        new RetrievalProfileDescriptor
        {
            Id = RetrievalProfileIds.Discovery,
            Label = "Semantic discovery",
            Description = "Hybrid lexical/vector retrieval for exploratory recall and topic discovery over conventional RAG chunk collections.",
            SearchMode = SearchModes.Hybrid,
            RequiresVector = true,
            RecommendedFor = new List<string> { "topic discovery", "semantic exploration", "broad recall" },
            Cautions = new List<string> { "Assumes a contentEmbedding vector field unless the request overrides embedding/vectorFields.", "Do not use as the default for verified evidence until evaluated." },
            Defaults = new RetrievalProfileDefaults
            {
                SearchMode = SearchModes.Hybrid,
                Embedding = new EmbeddingOptions { Field = "contentEmbedding", Purpose = EmbeddingPurposes.Query },
                Lexical = BuildRagLexical(),
                Hybrid = new HybridSearchOptions
                {
                    VectorWeight = 0.35f,
                    LexicalWeight = 0.65f,
                    CandidateMultiplier = 8,
                    Fusion = FusionModes.Rrf,
                    RrfK = 60
                },
                Limit = 10
            }
        },
        new RetrievalProfileDescriptor
        {
            Id = RetrievalProfileIds.ProductOptimization,
            Label = "Product optimization",
            Description = "Hybrid retrieval for product copy work across listings, keywords, reviews, competitor notes, manuals, and research artifacts.",
            SearchMode = SearchModes.Hybrid,
            RequiresVector = true,
            RecommendedFor = new List<string> { "product copy optimization", "keyword research", "review/OEM/manual synthesis" },
            Cautions = new List<string> { "Assumes a contentEmbedding vector field unless the request overrides embedding/vectorFields.", "Review retrieved claims before using them in generated copy." },
            Defaults = new RetrievalProfileDefaults
            {
                SearchMode = SearchModes.Hybrid,
                Embedding = new EmbeddingOptions { Field = "contentEmbedding", Purpose = EmbeddingPurposes.Query },
                Lexical = new LexicalSearchOptions
                {
                    Fields = ProductOptimizationFields.ToList(),
                    FieldBoosts = new Dictionary<string, float>(StringComparer.Ordinal)
                    {
                        ["/metadata/keywords"] = 2.0f,
                        ["/content/title"] = 1.5f,
                        ["/content/bullets"] = 1.35f,
                        ["/metadata/brand"] = 1.2f
                    },
                    ScanLimit = 5000,
                    Scoring = LexicalScorings.Bm25,
                    PrefixMatching = true,
                    PrefixMinChars = 3
                },
                Hybrid = new HybridSearchOptions
                {
                    VectorWeight = 0.45f,
                    LexicalWeight = 0.55f,
                    CandidateMultiplier = 8,
                    Fusion = FusionModes.Rrf,
                    RrfK = 60
                },
                Limit = 12
            }
        }
    };

    private static LexicalSearchOptions BuildEvidenceLexical() => new()
    {
        Fields = VerifiedEvidenceFields.ToList(),
        FieldBoosts = new Dictionary<string, float>(VerifiedEvidenceFieldBoosts, StringComparer.Ordinal),
        ScanLimit = 5000,
        Scoring = LexicalScorings.Bm25,
        PrefixMatching = true,
        PrefixMinChars = 3
    };

    private static LexicalSearchOptions BuildRagLexical() => new()
    {
        Fields = RagFields.ToList(),
        ScanLimit = 5000,
        Scoring = LexicalScorings.Bm25,
        PrefixMatching = true,
        PrefixMinChars = 3
    };

    private static RetrievalRequest Clone(RetrievalRequest source) => new()
    {
        Profile = source.Profile,
        Query = source.Query,
        Collections = source.Collections?.ToList() ?? new List<string>(),
        PartitionKeys = source.PartitionKeys?.ToList(),
        Filter = source.Filter,
        Embedding = CloneEmbedding(source.Embedding),
        VectorFields = source.VectorFields?.Select(CloneVectorField).ToList(),
        SearchMode = source.SearchMode,
        Lexical = CloneLexical(source.Lexical),
        Hybrid = CloneHybrid(source.Hybrid),
        Rerank = CloneRerank(source.Rerank),
        Limit = source.Limit,
        MinScore = source.MinScore,
        IncludeTrace = source.IncludeTrace
    };

    private static RetrievalVectorFieldQuery CloneVectorField(RetrievalVectorFieldQuery source) => new()
    {
        Field = source.Field,
        Weight = source.Weight,
        Query = source.Query,
        Embedding = CloneEmbedding(source.Embedding),
        CandidateLimit = source.CandidateLimit,
        MinScore = source.MinScore
    };

    private static EmbeddingOptions? CloneEmbedding(EmbeddingOptions? source) => source is null
        ? null
        : new EmbeddingOptions
        {
            Field = source.Field,
            Purpose = source.Purpose,
            QueryPrefix = source.QueryPrefix,
            PassagePrefix = source.PassagePrefix,
            SymmetricPrefix = source.SymmetricPrefix
        };

    private static LexicalSearchOptions? CloneLexical(LexicalSearchOptions? source) => source is null
        ? null
        : new LexicalSearchOptions
        {
            Query = source.Query,
            Fields = source.Fields?.ToList(),
            Top = source.Top,
            ScanLimit = source.ScanLimit,
            MinScore = source.MinScore,
            Scoring = source.Scoring,
            MatchMode = source.MatchMode,
            FieldBoosts = source.FieldBoosts is null ? null : new Dictionary<string, float>(source.FieldBoosts, StringComparer.Ordinal),
            Bm25K1 = source.Bm25K1,
            Bm25B = source.Bm25B,
            PhraseBoost = source.PhraseBoost,
            ExactBoost = source.ExactBoost,
            MetadataBoost = source.MetadataBoost,
            PrefixMatching = source.PrefixMatching,
            PrefixMinChars = source.PrefixMinChars,
            RequiredPhraseGroups = source.RequiredPhraseGroups?
                .Select(group => group.ToList())
                .ToList()
        };

    private static HybridSearchOptions? CloneHybrid(HybridSearchOptions? source) => source is null
        ? null
        : new HybridSearchOptions
        {
            VectorWeight = source.VectorWeight,
            LexicalWeight = source.LexicalWeight,
            CandidateMultiplier = source.CandidateMultiplier,
            VectorCandidateLimit = source.VectorCandidateLimit,
            LexicalCandidateLimit = source.LexicalCandidateLimit,
            Fusion = source.Fusion,
            RrfK = source.RrfK
        };

    private static RerankOptions? CloneRerank(RerankOptions? source) => source is null
        ? null
        : new RerankOptions
        {
            Enabled = source.Enabled,
            Provider = source.Provider,
            Mode = source.Mode,
            CandidateLimit = source.CandidateLimit,
            MaxCandidateChars = source.MaxCandidateChars,
            ContentField = source.ContentField,
            RerankScoreWeight = source.RerankScoreWeight,
            OriginalScoreWeight = source.OriginalScoreWeight,
            TimeoutSeconds = source.TimeoutSeconds,
            MaxOutputBytes = source.MaxOutputBytes,
            FallbackOnFailure = source.FallbackOnFailure
        };
}
