using Vyral.Providers.Abstractions;
using Vyral.Providers.Onnx;

namespace Vyral.Tests.Providers;

public class OnnxCrossEncoderRerankerProviderTargetTests
{
    [Fact]
    public async Task OnnxRerankerProvider_ExposesLocalSemanticRerankCapability()
    {
        var provider = OnnxCrossEncoderRerankerProviderTargets.CreateCpu();
        var gpuProvider = OnnxCrossEncoderRerankerProviderTargets.CreateGpu();

        Assert.Equal(OnnxCrossEncoderRerankerProviderTargets.CpuProviderId, provider.Profile.Id);
        Assert.Equal("onnx", provider.Profile.Family);
        Assert.True(provider.Profile.Local);
        Assert.False(provider.Profile.RequiresNetwork);
        Assert.Contains(ProviderCapabilityIds.AiRerank, provider.Capabilities.Select(c => c.Id));

        var capability = Assert.Single(provider.Capabilities);
        Assert.Equal(ProviderCapabilityIds.AiRerank, capability.Id);
        Assert.DoesNotContain("semantic_cross_encoder", capability.UnsupportedFeatures);

        var catalog = await provider.ListModelsAsync();

        Assert.Equal(ProviderModelCatalogStatuses.Succeeded, catalog.Status);
        Assert.Equal(OnnxCrossEncoderRerankerProviderTargets.DefaultCpuModelId, catalog.DefaultModelId);
        var model = Assert.Single(catalog.Items);
        Assert.Equal(OnnxCrossEncoderRerankerProviderTargets.DefaultCpuModelId, model.Id);
        Assert.True((bool)model.Metadata["semantic"]!);
        Assert.Equal("onnx-cross-encoder", model.Metadata["algorithm"]);

        var gpuCatalog = await gpuProvider.ListModelsAsync();
        var gpuModel = Assert.Single(gpuCatalog.Items);
        Assert.Equal(OnnxCrossEncoderRerankerProviderTargets.DefaultGpuModelId, gpuCatalog.DefaultModelId);
        Assert.Equal("cudaPreferred", gpuModel.Metadata["executionProvider"]);
        Assert.False((bool)gpuModel.Metadata["cpuOnly"]!);
    }

    [Fact]
    public async Task OnnxRerankerProvider_DoctorReportsMissingUntrackedModelFiles()
    {
        var provider = new OnnxCrossEncoderRerankerProviderTarget(new OnnxCrossEncoderRerankerProviderOptions
        {
            ProviderId = "test-onnx-reranker",
            DisplayName = "Test ONNX reranker",
            ModelId = "missing-model",
            ModelPath = ".vyral/models/missing-cross-encoder/onnx/model.onnx",
            VocabPath = ".vyral/models/missing-cross-encoder/vocab.txt",
            ExecutionProvider = "cpu",
            MaxTokens = 128,
            BatchSize = 2,
            Lowercase = true,
            ScoreMode = "auto",
            CpuOnly = true
        });

        var doctor = await provider.DiagnoseAsync();

        Assert.Equal(ProviderDoctorStatuses.Failed, doctor.Status);
        Assert.Contains(doctor.Checks, check => check.Id == "model.file" && check.Status == ProviderDoctorStatuses.Failed);
        Assert.Contains(doctor.Checks, check => check.Id == "tokenizer.vocab" && check.Status == ProviderDoctorStatuses.Failed);
    }

    [Fact]
    public async Task OnnxRerankerProvider_ReturnsNotConfiguredWhenModelFilesAreMissing()
    {
        var provider = new OnnxCrossEncoderRerankerProviderTarget(new OnnxCrossEncoderRerankerProviderOptions
        {
            ProviderId = "test-onnx-reranker",
            DisplayName = "Test ONNX reranker",
            ModelId = "missing-model",
            ModelPath = ".vyral/models/missing-cross-encoder/onnx/model.onnx",
            VocabPath = ".vyral/models/missing-cross-encoder/vocab.txt",
            ExecutionProvider = "cpu",
            MaxTokens = 128,
            BatchSize = 2,
            Lowercase = true,
            ScoreMode = "auto",
            CpuOnly = true
        });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiRerank,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiRerankRequest
            {
                Query = "retention hold release",
                Candidates = new List<AiRerankCandidate>
                {
                    new() { Id = "match", Text = "records retention hold release" },
                    new() { Id = "other", Text = "travel reimbursement" }
                },
                Limit = 1
            })
        });

        Assert.Equal(ProviderRunStatus.NotConfigured, result.Status);
        Assert.Equal(ProviderFailureClasses.Configuration, result.FailureClass);
        Assert.Equal("model_files_missing", result.ProviderStatus);
    }

    [OnnxRerankerModelFact]
    public async Task OnnxRerankerProvider_ReranksCandidatesWithUntrackedModel()
    {
        var modelDirectory = ResolveModelDirectory(Environment.GetEnvironmentVariable("VYRAL_ONNX_RERANK_MODEL_DIR")!);
        var provider = new OnnxCrossEncoderRerankerProviderTarget(new OnnxCrossEncoderRerankerProviderOptions
        {
            ProviderId = "live-onnx-reranker",
            DisplayName = "Live ONNX reranker",
            ModelId = "live-onnx-reranker",
            ModelPath = modelDirectory,
            ExecutionProvider = Environment.GetEnvironmentVariable("VYRAL_ONNX_RERANK_EXECUTION_PROVIDER") ?? "cpu",
            MaxTokens = ParseOptionalInt(Environment.GetEnvironmentVariable("VYRAL_ONNX_RERANK_MAX_TOKENS")) ?? 128,
            BatchSize = ParseOptionalInt(Environment.GetEnvironmentVariable("VYRAL_ONNX_RERANK_BATCH_SIZE")) ?? 2,
            Lowercase = true,
            ScoreMode = Environment.GetEnvironmentVariable("VYRAL_ONNX_RERANK_SCORE_MODE") ?? "auto",
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            CpuOnly = true
        });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiRerank,
            Operation = "run",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiRerankRequest
            {
                Query = "when can retained records be deleted after release",
                Candidates = new List<AiRerankCandidate>
                {
                    new() { Id = "travel", Text = "travel reimbursement requires receipts and manager approval" },
                    new() { Id = "retention", Text = "records subject to a retention hold may be deleted only after an authorized release" }
                },
                Limit = 2
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal("retention", result.Output["items"]!.AsArray()[0]!["id"]!.GetValue<string>());
    }

    private static string ResolveModelDirectory(string configured)
    {
        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Vyral.sln")))
        {
            directory = directory.Parent;
        }

        return Path.GetFullPath(Path.Combine(directory?.FullName ?? Directory.GetCurrentDirectory(), configured));
    }

    private static int? ParseOptionalInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }
}

public sealed class OnnxRerankerModelFactAttribute : FactAttribute
{
    public OnnxRerankerModelFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_ONNX_RERANK_MODEL_DIR")))
        {
            Skip = "Set VYRAL_ONNX_RERANK_MODEL_DIR to an untracked ONNX cross-encoder model directory to run reranker live tests.";
        }
    }
}
