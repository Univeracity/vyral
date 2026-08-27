using System.Security.Cryptography;
using System.Text;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Local;

namespace Vyral.Tests.Local;

public sealed class LocalGenerationBoundRecordSearchProjectionTests
{
    private static readonly RecordCollectionPolicy Policy = new() { Name = "library" };

    [Fact]
    public async Task ActiveSwitchKeepsExistingContinuationPinnedToRetainedGeneration()
    {
        var projection = CreateProjection();
        var firstGeneration = CreateGeneration("generation-a", "portable contracts", "portable retrieval");
        var secondGeneration = CreateGeneration("generation-b", "portable execution", "portable evidence");
        projection.PublishGeneration(firstGeneration);
        projection.ActivateGeneration(Policy.Name, firstGeneration.Descriptor.GenerationId);

        var request = SearchRequest(limit: 1);
        var firstPage = await projection.SearchGenerationAsync(Policy, request);
        Assert.Equal(RecordSearchProjectionResultStatuses.Succeeded, firstPage.Status);
        Assert.Equal("generation-a", firstPage.GenerationId);
        Assert.Equal("a-1", Assert.Single(firstPage.Items).Id);
        Assert.NotNull(firstPage.ContinuationToken);

        projection.PublishGeneration(secondGeneration);
        projection.ActivateGeneration(Policy.Name, secondGeneration.Descriptor.GenerationId);

        request.Query.ContinuationToken = firstPage.ContinuationToken;
        var retainedPage = await projection.SearchGenerationAsync(Policy, request);
        Assert.Equal(RecordSearchProjectionResultStatuses.Succeeded, retainedPage.Status);
        Assert.Equal("generation-a", retainedPage.GenerationId);
        Assert.Equal("b-1", Assert.Single(retainedPage.Items).Id);

        var fresh = await projection.SearchGenerationAsync(Policy, SearchRequest(limit: 1));
        Assert.Equal("generation-b", fresh.GenerationId);
        Assert.Equal("a-1", Assert.Single(fresh.Items).Id);
    }

    [Fact]
    public async Task RetiredContinuationFailsInsteadOfRemappingToActiveGeneration()
    {
        var projection = CreateProjection();
        var firstGeneration = CreateGeneration("generation-a", "portable contracts", "portable retrieval");
        var secondGeneration = CreateGeneration("generation-b", "portable execution", "portable evidence");
        projection.PublishGeneration(firstGeneration);
        projection.ActivateGeneration(Policy.Name, "generation-a");
        var firstPage = await projection.SearchGenerationAsync(Policy, SearchRequest(limit: 1));

        projection.PublishGeneration(secondGeneration);
        projection.ActivateGeneration(Policy.Name, "generation-b");
        projection.RetireGeneration(Policy.Name, "generation-a");

        var request = SearchRequest(limit: 1);
        request.Query.ContinuationToken = firstPage.ContinuationToken;
        var result = await projection.SearchGenerationAsync(Policy, request);

        Assert.Equal(RecordSearchProjectionResultStatuses.Failed, result.Status);
        Assert.Equal(RecordSearchProjectionFailureCodes.GenerationRetired, result.Failure!.Code);
        Assert.Equal("generation-a", result.GenerationId);
        Assert.Empty(result.Items);
        Assert.Null(result.ContinuationToken);
        Assert.Equal(RecordSearchProjectionCoverageStatuses.Unavailable, result.Coverage.Status);
    }

    [Fact]
    public async Task MissingPartitionFailsClosedWithoutPartialCandidates()
    {
        var projection = CreateProjection();
        var generation = CreateGeneration("generation-a", "portable contracts", "portable retrieval");
        projection.PublishGeneration(generation);
        projection.ActivateGeneration(Policy.Name, "generation-a");
        projection.SetAvailablePartitions(Policy.Name, "generation-a", ["public-a"]);

        var result = await projection.SearchGenerationAsync(Policy, SearchRequest(limit: 10));

        Assert.Equal(RecordSearchProjectionResultStatuses.Failed, result.Status);
        Assert.Equal(RecordSearchProjectionFailureCodes.CoverageIncomplete, result.Failure!.Code);
        Assert.Equal(RecordSearchProjectionCoverageStatuses.Incomplete, result.Coverage.Status);
        Assert.Equal(["public-a"], result.Coverage.CoveredPartitions);
        Assert.Equal(["public-b"], result.Coverage.MissingPartitions);
        Assert.Empty(result.Items);
        Assert.Null(result.ContinuationToken);

        var inspection = await projection.InspectGenerationAsync(Policy);
        Assert.NotNull(inspection);
        Assert.Equal(RecordSearchProjectionCoverageStatuses.Incomplete, inspection!.CoverageStatus);
    }

    [Fact]
    public async Task TamperedOrCrossRequestContinuationFailsExplicitly()
    {
        var projection = CreateProjection();
        var generation = CreateGeneration("generation-a", "portable contracts", "portable retrieval");
        projection.PublishGeneration(generation);
        projection.ActivateGeneration(Policy.Name, "generation-a");
        var first = await projection.SearchGenerationAsync(Policy, SearchRequest(limit: 1));

        var tamperedRequest = SearchRequest(limit: 1);
        var token = first.ContinuationToken!;
        var signatureStart = token.IndexOf('.', StringComparison.Ordinal) + 1;
        var replacement = token[signatureStart] == 'A' ? 'B' : 'A';
        tamperedRequest.Query.ContinuationToken =
            token[..signatureStart] + replacement + token[(signatureStart + 1)..];
        var tampered = await projection.SearchGenerationAsync(Policy, tamperedRequest);
        Assert.Equal(RecordSearchProjectionFailureCodes.InvalidContinuation, tampered.Failure!.Code);

        var changedRequest = SearchRequest(limit: 1, query: "retrieval");
        changedRequest.Query.ContinuationToken = token;
        var changed = await projection.SearchGenerationAsync(Policy, changedRequest);
        Assert.Equal(RecordSearchProjectionFailureCodes.InvalidContinuation, changed.Failure!.Code);

        var oversizedRequest = SearchRequest(limit: 1);
        oversizedRequest.Query.ContinuationToken = new string('x', 8193);
        var oversized = await projection.SearchGenerationAsync(Policy, oversizedRequest);
        Assert.Equal(RecordSearchProjectionFailureCodes.InvalidContinuation, oversized.Failure!.Code);
    }

    [Fact]
    public async Task ExpiredContinuationFailsExplicitly()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var projection = CreateProjection(clock, TimeSpan.FromMinutes(5));
        projection.PublishGeneration(CreateGeneration("generation-a", "portable contracts", "portable retrieval"));
        projection.ActivateGeneration(Policy.Name, "generation-a");
        var first = await projection.SearchGenerationAsync(Policy, SearchRequest(limit: 1));
        clock.UtcNow = clock.UtcNow.AddMinutes(6);

        var request = SearchRequest(limit: 1);
        request.Query.ContinuationToken = first.ContinuationToken;
        var expired = await projection.SearchGenerationAsync(Policy, request);

        Assert.Equal(RecordSearchProjectionFailureCodes.ExpiredContinuation, expired.Failure!.Code);
        Assert.Empty(expired.Items);
    }

    [Fact]
    public async Task DescriptorFenceDeadlineAndWorkBoundFailWithoutCandidates()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var projection = CreateProjection(clock);
        var generation = CreateGeneration("generation-a", "portable contracts", "portable retrieval");
        projection.PublishGeneration(generation);
        projection.ActivateGeneration(Policy.Name, "generation-a");

        var fencedRequest = SearchRequest(limit: 10);
        fencedRequest.ExpectedDescriptorDigest = Hash("wrong");
        var fenced = await projection.SearchGenerationAsync(Policy, fencedRequest);
        Assert.Equal(RecordSearchProjectionFailureCodes.GenerationDescriptorMismatch, fenced.Failure!.Code);

        var expiredRequest = SearchRequest(limit: 10);
        expiredRequest.DeadlineUtc = clock.UtcNow.AddSeconds(-1).UtcDateTime;
        var expired = await projection.SearchGenerationAsync(Policy, expiredRequest);
        Assert.Equal(RecordSearchProjectionFailureCodes.DeadlineExceeded, expired.Failure!.Code);

        var boundedRequest = SearchRequest(limit: 10);
        boundedRequest.Query.Lexical!.ScanLimit = 1;
        var bounded = await projection.SearchGenerationAsync(Policy, boundedRequest);
        Assert.Equal(RecordSearchProjectionFailureCodes.WorkLimitExceeded, bounded.Failure!.Code);
        Assert.Equal(3, bounded.Diagnostics.WorkUnits);
        Assert.Empty(bounded.Items);
    }

    [Fact]
    public async Task CallerSelectedScoringParametersFailInsteadOfBeingSilentlyIgnored()
    {
        var projection = CreateProjection();
        projection.PublishGeneration(CreateGeneration("generation-a", "portable contracts", "portable retrieval"));
        projection.ActivateGeneration(Policy.Name, "generation-a");
        var request = SearchRequest(limit: 10);
        request.Query.Lexical!.Bm25K1 = 2;

        var result = await projection.SearchGenerationAsync(Policy, request);

        Assert.Equal(RecordSearchProjectionResultStatuses.Failed, result.Status);
        Assert.Equal(RecordSearchProjectionFailureCodes.UnsupportedQuery, result.Failure!.Code);
        Assert.Contains("generation strategy", result.Failure.Message, StringComparison.Ordinal);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task HydrationPreservesProjectionEvidenceAndRejectsStaleCandidates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-generation-hydration-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(path);
        await store.InitializeAsync();
        await store.CreateCollectionAsync(Policy);
        await store.UpsertRecordAsync(Policy.Name, new VyralRecord
        {
            Id = "a-1",
            PartitionKey = "public-a",
            Type = "note",
            Content = new System.Text.Json.Nodes.JsonObject { ["text"] = "portable contracts" }
        });

        var projection = CreateProjection();
        var generation = CreateGeneration("generation-a", "portable contracts", "portable retrieval");
        projection.PublishGeneration(generation);
        projection.ActivateGeneration(Policy.Name, "generation-a");

        var hydrated = await projection.SearchGenerationAndHydrateAsync(
            store,
            Policy,
            SearchRequest(limit: 10));

        Assert.Equal("generation-a", hydrated.Projection.GenerationId);
        Assert.Equal(2, hydrated.Projection.Items.Count);
        Assert.Equal("a-1", Assert.Single(hydrated.Items).Record.Id);
        Assert.Equal(1, hydrated.StaleCandidatesDiscarded);
    }

    [Fact]
    public async Task CancellationAndInvalidPartialFailureAreRejected()
    {
        var projection = CreateProjection();
        projection.PublishGeneration(CreateGeneration("generation-a", "portable contracts", "portable retrieval"));
        projection.ActivateGeneration(Policy.Name, "generation-a");
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            projection.SearchGenerationAsync(Policy, SearchRequest(limit: 10), source.Token));

        var invalid = new GenerationBoundRecordSearchProjectionResult
        {
            Status = RecordSearchProjectionResultStatuses.Failed,
            Items = [new RecordSearchProjectionCandidate { PartitionKey = "public-a", Id = "a-1", Revision = 1, Score = 1 }],
            Coverage = new RecordSearchProjectionCoverage(),
            Diagnostics = new RecordSearchProjectionWorkDiagnostics { ReturnedCount = 1 },
            Failure = new RecordSearchProjectionFailure { Code = "failed", Message = "failed" }
        };
        Assert.Throws<InvalidOperationException>(() =>
            RecordSearchProjectionGenerationContract.ValidateResult(invalid));

        var sensitive = new GenerationBoundRecordSearchProjectionResult
        {
            Status = RecordSearchProjectionResultStatuses.Failed,
            Coverage = new RecordSearchProjectionCoverage(),
            Diagnostics = new RecordSearchProjectionWorkDiagnostics
            {
                Details = new System.Text.Json.Nodes.JsonObject { ["authorizationToken"] = "not-allowed" }
            },
            Failure = new RecordSearchProjectionFailure { Code = "failed", Message = "failed" }
        };
        Assert.Throws<InvalidOperationException>(() =>
            RecordSearchProjectionGenerationContract.ValidateResult(sensitive));

        Assert.Throws<InvalidOperationException>(() =>
            RecordSearchProjectionGenerationContract.ValidateBuildProgress(
                new RecordSearchProjectionGenerationBuildProgress
                {
                    Stage = "build",
                    Completed = 2,
                    Total = 1
                }));
        Assert.Throws<InvalidOperationException>(() =>
            RecordSearchProjectionGenerationContract.ValidateBuildProgress(
                new RecordSearchProjectionGenerationBuildProgress
                {
                    Stage = "build",
                    Completed = 1,
                    Total = 1,
                    Checkpoint = new System.Text.Json.Nodes.JsonObject
                    {
                        ["provider"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["accessToken"] = "not-allowed"
                        }
                    }
                }));
    }

    [Fact]
    public void RepublishingAnImmutableGenerationRequiresIdenticalContentAndCoverage()
    {
        var projection = CreateProjection();
        var original = CreateGeneration("generation-a", "portable contracts", "portable retrieval");
        projection.PublishGeneration(original);

        var reordered = CreateGeneration("generation-a", "portable contracts", "portable retrieval");
        reordered.Documents.Reverse();
        projection.PublishGeneration(reordered);

        var substituted = CreateGeneration("generation-a", "substituted content", "portable retrieval");
        Assert.Throws<InvalidOperationException>(() => projection.PublishGeneration(substituted));

        var changedCoverage = CreateGeneration("generation-a", "portable contracts", "portable retrieval");
        changedCoverage.AvailablePartitions = ["public-a"];
        Assert.Throws<InvalidOperationException>(() => projection.PublishGeneration(changedCoverage));
    }

    private static LocalGenerationBoundRecordSearchProjection CreateProjection(
        TimeProvider? timeProvider = null,
        TimeSpan? continuationLifetime = null) => new(new LocalGenerationBoundRecordSearchProjectionOptions
        {
            ContinuationSigningKey = SHA256.HashData(Encoding.UTF8.GetBytes("local-generation-bound-test-key")),
            TimeProvider = timeProvider ?? TimeProvider.System,
            ContinuationLifetime = continuationLifetime ?? TimeSpan.FromMinutes(15)
        });

    private static GenerationBoundRecordSearchProjectionRequest SearchRequest(int limit, string query = "portable") => new()
    {
        Query = new QueryEnvelope
        {
            PartitionKeys = ["public-a", "public-b"],
            Limit = limit,
            Lexical = new LexicalSearchOptions
            {
                Query = query,
                Top = limit,
                ScanLimit = 100,
                MatchMode = LexicalMatchModes.Any
            }
        }
    };

    private static LocalRecordSearchProjectionGeneration CreateGeneration(
        string generationId,
        string firstText,
        string secondText)
    {
        var descriptor = new RecordSearchProjectionGenerationDescriptor
        {
            Collection = Policy.Name,
            GenerationId = generationId,
            ProviderId = "local-exhaustive",
            ProfileId = "lexical-exhaustive-v1",
            StrategyVersion = "exhaustive-token-v1",
            SourceManifestDigest = Hash(generationId + ":manifest"),
            RecordRevisionSetDigest = Hash(generationId + ":records"),
            ProjectionSchemaDigest = Hash("projection-schema-v1"),
            AnalyzerDigest = Hash("analyzer-v1"),
            ConfigurationDigest = Hash("configuration-v1"),
            ExpectedItemCount = 3,
            ExpectedPartitions = ["public-a", "public-b"],
            Capabilities = ["completeCoverage", "generationPinnedContinuation", "lexical"],
            CreatedAtUtc = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc)
        };
        RecordSearchProjectionGenerationContract.SealDescriptor(descriptor);
        return new LocalRecordSearchProjectionGeneration
        {
            Descriptor = descriptor,
            Documents =
            [
                Document("public-a", "a-1", firstText),
                Document("public-a", "a-2", "provider native semantics"),
                Document("public-b", "b-1", secondText)
            ]
        };
    }

    private static LocalRecordSearchProjectionDocument Document(string partition, string id, string text) => new()
    {
        Candidate = new RecordSearchProjectionCandidate
        {
            PartitionKey = partition,
            Id = id,
            Revision = 1,
            Score = 1
        },
        SearchText = text
    };

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
