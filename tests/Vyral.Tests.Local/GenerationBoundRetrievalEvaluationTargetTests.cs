using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Local;

namespace Vyral.Tests.Local;

public sealed class GenerationBoundRetrievalEvaluationTargetTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"vyral-generation-evaluation-{Guid.NewGuid():N}.sqlite");
    private readonly RecordCollectionPolicy _policy = new() { Name = "library" };
    private SqliteRecordCollectionStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new SqliteRecordCollectionStore(_path);
        await _store.InitializeAsync();
        await _store.CreateCollectionAsync(_policy);
        await _store.UpsertRecordAsync(_policy.Name, Record("expected", "portable contracts"));
        await _store.UpsertRecordAsync(_policy.Name, Record("hard-negative", "unrelated provider"));
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ComparisonResolvesAndReportsExactGenerationTargets()
    {
        var projection = CreateProjection();
        var exhaustive = CreateGeneration("generation-exhaustive");
        var indexed = CreateGeneration("generation-indexed");
        projection.PublishGeneration(exhaustive);
        projection.PublishGeneration(indexed);
        var resolver = Resolver(
            Target("exhaustive", projection, exhaustive),
            Target("indexed", projection, indexed));
        var defaultService = new RejectingRetrievalService();
        var evaluation = new LocalRetrievalEvaluationService(defaultService, resolver);

        var result = await evaluation.CompareAsync(Comparison(
            Variant("exhaustive-arm", "exhaustive", exhaustive),
            Variant("indexed-arm", "indexed", indexed)));

        Assert.Equal(2, result.VariantsSucceeded);
        Assert.Equal(0, result.VariantsFailed);
        Assert.Equal(2, result.Variants.Count);
        Assert.Equal(0, defaultService.Calls);
        foreach (var variant in result.Variants)
        {
            Assert.Equal(EvaluationVariantStatuses.Succeeded, variant.Status);
            Assert.NotNull(variant.Target);
            Assert.Equal(1, variant.Metrics.HitRate);
            Assert.Equal(0, variant.Metrics.HardNegativeHitRate);
            Assert.Equal(1, variant.Metrics.Succeeded);
            Assert.Equal(0, variant.Metrics.Failed);
            var testCase = Assert.Single(variant.Cases);
            Assert.Equal("expected", Assert.Single(testCase.TopResults).Id);
        }
        Assert.Equal("generation-exhaustive", result.Variants[0].Target!.GenerationId);
        Assert.Equal(exhaustive.Descriptor.DescriptorDigest, result.Variants[0].Target!.GenerationDescriptorDigest);
        Assert.Equal("generation-indexed", result.Variants[1].Target!.GenerationId);
        Assert.Equal(indexed.Descriptor.DescriptorDigest, result.Variants[1].Target!.GenerationDescriptorDigest);
    }

    [Fact]
    public async Task DescriptorSubstitutionFailsTheVariantWithoutUsingDefaultTarget()
    {
        var projection = CreateProjection();
        var generation = CreateGeneration("generation-a");
        projection.PublishGeneration(generation);
        var defaultService = new RejectingRetrievalService();
        var evaluation = new LocalRetrievalEvaluationService(
            defaultService,
            Resolver(Target("registered", projection, generation)));
        var variant = Variant("candidate", "registered", generation);
        variant.Target!.ExpectedGenerationDescriptorDigest = Hash("substituted-descriptor");

        var result = await evaluation.CompareAsync(Comparison(variant));

        var failed = Assert.Single(result.Variants);
        Assert.Equal(EvaluationVariantStatuses.Failed, failed.Status);
        Assert.Contains("does not match the expected generation descriptor", failed.Error, StringComparison.Ordinal);
        Assert.Null(failed.Target);
        Assert.Equal(0, defaultService.Calls);
    }

    [Fact]
    public async Task StaleCandidateRevisionFailsTheTargetInsteadOfChangingMetrics()
    {
        var projection = CreateProjection();
        var generation = CreateGeneration("generation-stale", expectedRevision: 2);
        projection.PublishGeneration(generation);
        var evaluation = new LocalRetrievalEvaluationService(
            new RejectingRetrievalService(),
            Resolver(Target("stale", projection, generation)));

        var result = await evaluation.CompareAsync(Comparison(
            Variant("stale-arm", "stale", generation)));

        var failed = Assert.Single(result.Variants);
        Assert.Equal(EvaluationVariantStatuses.Failed, failed.Status);
        Assert.Contains("failed 1 case", failed.Error, StringComparison.Ordinal);
        Assert.Equal(0, failed.Metrics.Succeeded);
        Assert.Null(failed.Target);
    }

    [Fact]
    public async Task MissingLogicalCoverageFailsTheTargetWithoutPartialCandidates()
    {
        var projection = CreateProjection();
        var generation = CreateGeneration("generation-incomplete");
        projection.PublishGeneration(generation);
        projection.SetAvailablePartitions(_policy.Name, generation.Descriptor.GenerationId, []);
        var evaluation = new LocalRetrievalEvaluationService(
            new RejectingRetrievalService(),
            Resolver(Target("incomplete", projection, generation)));

        var result = await evaluation.CompareAsync(Comparison(
            Variant("incomplete-arm", "incomplete", generation)));

        var failed = Assert.Single(result.Variants);
        Assert.Equal(EvaluationVariantStatuses.Failed, failed.Status);
        Assert.Contains("failed 1 case", failed.Error, StringComparison.Ordinal);
        Assert.Null(failed.Target);
    }

    [Fact]
    public void ExperimentalTargetsDoNotSilentlyExpandThePublicJsonContract()
    {
        var variant = new RetrievalEvaluationVariant
        {
            Id = "candidate",
            Target = new RetrievalEvaluationTargetReference { Id = "registered" }
        };
        var result = new RetrievalEvaluationVariantResult
        {
            Id = "candidate",
            Target = new RetrievalEvaluationTargetEvidence { Id = "registered" }
        };

        Assert.DoesNotContain("target", JsonSerializer.Serialize(variant), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("target", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    private GenerationBoundRetrievalEvaluationTargetResolver Resolver(
        params GenerationBoundRetrievalEvaluationTargetRegistration[] targets) => new(targets);

    private GenerationBoundRetrievalEvaluationTargetRegistration Target(
        string id,
        IGenerationBoundRecordSearchProjection projection,
        LocalRecordSearchProjectionGeneration generation) => new()
        {
            Id = id,
            Projection = projection,
            CanonicalStore = _store,
            Policy = _policy,
            GenerationId = generation.Descriptor.GenerationId,
            GenerationDescriptorDigest = generation.Descriptor.DescriptorDigest
        };

    private static RetrievalEvaluationComparisonRequest Comparison(
        params RetrievalEvaluationVariant[] variants) => new()
        {
            Cases =
            [
                new RetrievalEvaluationCase
                {
                    Name = "portable-contracts",
                    Request = new RetrievalRequest
                    {
                        Query = "portable",
                        Collections = ["library"],
                        SearchMode = SearchModes.Lexical,
                        Limit = 5
                    },
                    Expected = [new RetrievalEvaluationExpectedMatch { Id = "expected" }],
                    HardNegatives =
                    [
                        new RetrievalEvaluationHardNegativeMatch
                        {
                            Id = "hard-negative",
                            Reason = "must not match a portable-contract query"
                        }
                    ],
                    K = 5
                }
            ],
            Variants = variants.ToList(),
            IncludeTopResults = true,
            IncludeCaseResults = true
        };

    private static RetrievalEvaluationVariant Variant(
        string variantId,
        string targetId,
        LocalRecordSearchProjectionGeneration generation) => new()
        {
            Id = variantId,
            Target = new RetrievalEvaluationTargetReference
            {
                Id = targetId,
                GenerationId = generation.Descriptor.GenerationId,
                ExpectedGenerationDescriptorDigest = generation.Descriptor.DescriptorDigest
            }
        };

    private LocalGenerationBoundRecordSearchProjection CreateProjection() => new(
        new LocalGenerationBoundRecordSearchProjectionOptions
        {
            ContinuationSigningKey = SHA256.HashData(
                Encoding.UTF8.GetBytes("generation-evaluation-test-key")),
            DefaultWorkLimit = 100,
            MaxWorkLimit = 100
        });

    private LocalRecordSearchProjectionGeneration CreateGeneration(
        string generationId,
        int expectedRevision = 1)
    {
        var descriptor = new RecordSearchProjectionGenerationDescriptor
        {
            Collection = _policy.Name,
            GenerationId = generationId,
            ProviderId = "local-test",
            ProfileId = "lexical-test-v1",
            StrategyVersion = "token-test-v1",
            SourceManifestDigest = Hash(generationId + ":manifest"),
            RecordRevisionSetDigest = Hash(generationId + ":records"),
            ProjectionSchemaDigest = Hash("projection-schema"),
            AnalyzerDigest = Hash("analyzer"),
            ConfigurationDigest = Hash("configuration"),
            ExpectedItemCount = 2,
            ExpectedPartitions = ["public"],
            Capabilities = ["completeCoverage", "generationPinnedContinuation", "lexical"],
            CreatedAtUtc = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc)
        };
        RecordSearchProjectionGenerationContract.SealDescriptor(descriptor);
        return new LocalRecordSearchProjectionGeneration
        {
            Descriptor = descriptor,
            Documents =
            [
                new LocalRecordSearchProjectionDocument
                {
                    Candidate = new RecordSearchProjectionCandidate
                    {
                        PartitionKey = "public",
                        Id = "expected",
                        Revision = expectedRevision,
                        Score = 1
                    },
                    SearchText = "portable portable contracts"
                },
                new LocalRecordSearchProjectionDocument
                {
                    Candidate = new RecordSearchProjectionCandidate
                    {
                        PartitionKey = "public",
                        Id = "hard-negative",
                        Revision = 1,
                        Score = 1
                    },
                    SearchText = "unrelated provider"
                }
            ]
        };
    }

    private static VyralRecord Record(string id, string text) => new()
    {
        Id = id,
        PartitionKey = "public",
        Type = "note",
        Content = new JsonObject { ["text"] = text }
    };

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class RejectingRetrievalService : IRetrievalService
    {
        public int Calls { get; private set; }

        public Task<RetrievalResultEnvelope> SearchAsync(
            RetrievalRequest request,
            CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("The default retrieval service must not be used.");
        }
    }
}
