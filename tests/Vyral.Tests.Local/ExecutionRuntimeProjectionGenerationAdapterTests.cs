using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;
using Vyral.Execution.Local;
using Vyral.Local;
using Vyral.Server;

namespace Vyral.Tests.Local;

public sealed class ExecutionRuntimeProjectionGenerationAdapterTests
{
    [Fact]
    public async Task BuildRunIsDurableIdempotentCheckpointedAndArtifactBound()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vyral-projection-generation-{Guid.NewGuid():N}");
        var runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = Path.Combine(root, "execution.sqlite"),
            ArtifactDirectory = Path.Combine(root, "execution-artifacts")
        });
        var objects = new FileObjectStore(Path.Combine(root, "objects"));
        var builder = new FixtureGenerationBuilder();
        var adapter = new ExecutionRuntimeProjectionGenerationAdapter(runtime, builder, objects);
        var request = BuildRequest();

        var admitted = await adapter.StartBuildAsync(request, "library:generation-a");
        var replayed = await adapter.StartBuildAsync(request, "library:generation-a");
        var completed = await WaitForRunAsync(runtime, admitted.Id, ExecutionRunStatuses.Succeeded);

        Assert.Equal(admitted.Id, replayed.Id);
        Assert.True(replayed.AdmissionReplayed);
        Assert.Equal(1, builder.Calls);
        var receipt = completed.Result!.Deserialize<RecordSearchProjectionGenerationBuildReceipt>(ExecutionJson.Options)!;
        RecordSearchProjectionGenerationContract.ValidateBuildReceipt(request, receipt);
        Assert.Equal("generation-a", receipt.Descriptor.GenerationId);
        Assert.Single(receipt.Descriptor.Artifacts);

        var checkpoint = await runtime.GetCheckpointAsync(admitted.Id, "projection-generation-progress");
        Assert.NotNull(checkpoint);
        Assert.Equal("verify", checkpoint!.Content!["stage"]!.GetValue<string>());
        var runArtifacts = await runtime.ListArtifactsAsync(admitted.Id);
        var terminal = Assert.Single(runArtifacts);
        Assert.Equal("projection-generation-receipt", terminal.Name);
        Assert.Equal(receipt.Descriptor.DescriptorDigest, terminal.Metadata["descriptorDigest"]);

        var objectReceipt = Assert.Single(receipt.Descriptor.Artifacts);
        var stored = await objects.GetObjectAsync(new ObjectReadRequest
        {
            Container = "retrieval",
            Key = "generations/generation-a/sha256/" + objectReceipt.ContentHash[7..]
        });
        Assert.NotNull(stored);
        await stored!.Content.DisposeAsync();
        Assert.Equal(objectReceipt.ContentHash, stored.ContentHash);
    }

    [Fact]
    public async Task AdapterRejectsMissingIdempotencyAndMismatchedBuilderBeforeAdmission()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vyral-projection-generation-{Guid.NewGuid():N}");
        var runtime = new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = Path.Combine(root, "execution.sqlite")
        });
        var adapter = new ExecutionRuntimeProjectionGenerationAdapter(
            runtime,
            new FixtureGenerationBuilder(),
            new FileObjectStore(Path.Combine(root, "objects")));
        var request = BuildRequest();

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.StartBuildAsync(request, ""));
        request.BuilderId = "another-builder";
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.StartBuildAsync(request, "key"));
        request.BuilderId = FixtureGenerationBuilder.Id;
        request.SourceManifestRef = "https://objects.example/manifest?token=not-stable";
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.StartBuildAsync(request, "key"));
        Assert.Empty(await runtime.ListRunsAsync());
    }

    private static RecordSearchProjectionGenerationBuildRequest BuildRequest() => new()
    {
        Collection = "library",
        GenerationId = "generation-a",
        BuilderId = FixtureGenerationBuilder.Id,
        ProviderId = "local-exhaustive",
        ProfileId = "lexical-exhaustive-v1",
        StrategyVersion = "exhaustive-token-v1",
        SourceManifestRef = "manifests/library-generation-a.json",
        SourceManifestDigest = Hash("manifest"),
        ExpectedRecordRevisionSetDigest = Hash("record-revisions"),
        ProjectionSchemaDigest = Hash("projection-schema"),
        AnalyzerDigest = Hash("analyzer"),
        ConfigurationDigest = Hash("configuration"),
        ExpectedItemCount = 2,
        ExpectedPartitions = ["public-a", "public-b"],
        DeadlineUtc = DateTime.UtcNow.AddMinutes(5)
    };

    private static async Task<ExecutionRun> WaitForRunAsync(IExecutionRuntime runtime, string id, string status)
    {
        ExecutionRun? run = null;
        for (var i = 0; i < 200; i++)
        {
            run = await runtime.GetRunAsync(id);
            if (run?.Status == status)
            {
                return run;
            }
            await Task.Delay(25);
        }
        throw new InvalidOperationException($"Run {id} did not reach {status}; last status was {run?.Status} and error was {run?.Error}.");
    }

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class FixtureGenerationBuilder : IRecordSearchProjectionGenerationBuilder
    {
        public const string Id = "fixture-local-generation-builder";
        private int _calls;
        public string BuilderId => Id;
        public int Calls => Volatile.Read(ref _calls);

        public async Task<RecordSearchProjectionGenerationBuildReceipt> BuildAndVerifyAsync(
            RecordSearchProjectionGenerationBuildRequest request,
            IObjectStore artifactStore,
            Func<RecordSearchProjectionGenerationBuildProgress, CancellationToken, Task>? reportProgress = null,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            if (reportProgress is not null)
            {
                await reportProgress(new RecordSearchProjectionGenerationBuildProgress
                {
                    Stage = "build",
                    Completed = 1,
                    Total = 2
                }, ct);
            }
            var bytes = Encoding.UTF8.GetBytes("immutable local projection generation");
            var artifact = await artifactStore.PutContentAddressedAsync(
                "retrieval",
                "generations/" + request.GenerationId,
                bytes,
                "application/octet-stream",
                ct: ct);
            if (reportProgress is not null)
            {
                await reportProgress(new RecordSearchProjectionGenerationBuildProgress
                {
                    Stage = "verify",
                    Completed = 2,
                    Total = 2,
                    Checkpoint = new System.Text.Json.Nodes.JsonObject
                    {
                        ["artifactHash"] = artifact.ContentHash
                    }
                }, ct);
            }
            var descriptor = new RecordSearchProjectionGenerationDescriptor
            {
                Collection = request.Collection,
                GenerationId = request.GenerationId,
                ProviderId = request.ProviderId,
                ProfileId = request.ProfileId,
                StrategyVersion = request.StrategyVersion,
                SourceManifestDigest = request.SourceManifestDigest,
                RecordRevisionSetDigest = request.ExpectedRecordRevisionSetDigest!,
                ProjectionSchemaDigest = request.ProjectionSchemaDigest,
                AnalyzerDigest = request.AnalyzerDigest,
                ConfigurationDigest = request.ConfigurationDigest,
                ExpectedItemCount = request.ExpectedItemCount,
                ExpectedPartitions = request.ExpectedPartitions.ToList(),
                Capabilities = ["completeCoverage", "generationPinnedContinuation", "lexical"],
                Artifacts =
                [
                    new RecordSearchProjectionGenerationArtifact
                    {
                        Id = "index",
                        Kind = "index-part",
                        ContentHash = artifact.ContentHash,
                        SizeBytes = artifact.Object.ContentLength,
                        MediaType = artifact.Object.ContentType
                    }
                ],
                CreatedAtUtc = DateTime.UtcNow
            };
            RecordSearchProjectionGenerationContract.SealDescriptor(descriptor);
            return new RecordSearchProjectionGenerationBuildReceipt
            {
                BuilderId = BuilderId,
                Descriptor = descriptor,
                EvaluationReceiptDigest = Hash("evaluation"),
                BuiltAtUtc = DateTime.UtcNow
            };
        }
    }
}
