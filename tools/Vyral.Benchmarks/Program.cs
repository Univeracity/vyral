using System.Diagnostics;
using System.Text.Json;
using Vyral.Abstractions.Models;
using Vyral.Embeddings.Onnx;

var options = BenchmarkOptions.Parse(args);
var benchmark = await OnnxEmbeddingBenchmark.RunAsync(options);
Console.WriteLine(JsonSerializer.Serialize(benchmark));

internal sealed class BenchmarkOptions
{
    public string ModelPath { get; init; } = ".vyral/models/all-MiniLM-L6-v2-quantized";
    public string Provider { get; init; } = OnnxEmbeddingProviders.GenericProvider;
    public string ModelId { get; init; } = "benchmark-model";
    public string ExecutionProvider { get; init; } = "cpu";
    public int Dimensions { get; init; } = 384;
    public int MaxTokens { get; init; } = 256;
    public string Pooling { get; init; } = "mean";
    public int Warmup { get; init; } = 4;
    public int Iterations { get; init; } = 32;
    public int? IntraOpNumThreads { get; init; }
    public int? InterOpNumThreads { get; init; }
    public long? CudaMemoryLimitMb { get; init; }
    public string? QueryPrefix { get; init; }
    public string? PassagePrefix { get; init; }
    public string? SymmetricPrefix { get; init; }

    public static BenchmarkOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var name = arg[2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[name] = "true";
                continue;
            }

            values[name] = args[++i];
        }

        return new BenchmarkOptions
        {
            ModelPath = GetString(values, "model-path", ".vyral/models/all-MiniLM-L6-v2-quantized"),
            Provider = GetString(values, "provider", OnnxEmbeddingProviders.GenericProvider),
            ModelId = GetString(values, "model-id", "benchmark-model"),
            ExecutionProvider = GetString(values, "execution-provider", "cpu"),
            Dimensions = GetPositiveInt(values, "dimensions", 384),
            MaxTokens = GetPositiveInt(values, "max-tokens", 256),
            Pooling = GetString(values, "pooling", "mean"),
            Warmup = GetPositiveInt(values, "warmup", 4),
            Iterations = GetPositiveInt(values, "iterations", 32),
            IntraOpNumThreads = GetOptionalPositiveInt(values, "intra-op-threads"),
            InterOpNumThreads = GetOptionalPositiveInt(values, "inter-op-threads"),
            CudaMemoryLimitMb = GetOptionalPositiveLong(values, "cuda-memory-limit-mb"),
            QueryPrefix = GetOptionalString(values, "query-prefix"),
            PassagePrefix = GetOptionalString(values, "passage-prefix"),
            SymmetricPrefix = GetOptionalString(values, "symmetric-prefix")
        };
    }

    private static string GetString(Dictionary<string, string> values, string name, string defaultValue)
    {
        return values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : defaultValue;
    }

    private static string? GetOptionalString(Dictionary<string, string> values, string name)
    {
        return values.TryGetValue(name, out var value)
            ? value
            : null;
    }

    private static int GetPositiveInt(Dictionary<string, string> values, string name, int defaultValue)
    {
        var value = GetOptionalPositiveInt(values, name);
        return value ?? defaultValue;
    }

    private static int? GetOptionalPositiveInt(Dictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new InvalidOperationException($"--{name} must be a positive integer.");
        }

        return parsed;
    }

    private static long? GetOptionalPositiveLong(Dictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!long.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new InvalidOperationException($"--{name} must be a positive integer.");
        }

        return parsed;
    }
}

internal static class OnnxEmbeddingBenchmark
{
    private static readonly WorkloadItem[] Documents =
    {
        new("policy-data-retention", "Retention policy: protected records must be retained until an approved retention hold ends; hold releases permit deletion only after recorded approval."),
        new("policy-employee-retention", "People operations retention bonus program: eligibility, payout timing, and manager approval steps for employees staying through a reorganization."),
        new("onboarding-laptop", "New-hire onboarding checklist: order laptop hardware, enroll the device in MDM, create the SSO account, ship equipment, and confirm first-day access."),
        new("offboarding-access", "Employee offboarding runbook: disable SSO, revoke application access, collect assigned laptop hardware, forward email, and close the HR record."),
        new("invoice-reconciliation", "Accounts payable workflow: match invoice number, purchase order, receiving record, vendor tax information, and approval status before issuing payment."),
        new("vendor-contract", "Vendor contract checklist: security review, renewal notice dates, service-level terms, master service agreement status, and procurement owner."),
        new("incident-compromised-credentials", "Security incident runbook: compromised credentials require token revocation, password reset, active session termination, audit-log review, and user notification."),
        new("password-policy", "Password policy: minimum length, rotation exception handling, complexity rules, recovery questions, and multi-factor enrollment requirements."),
        new("cosmos-vector-policy", "Azure Cosmos DB vector search setup: container vector policy declares vector path, datatype, dimensions, distance function, and index type before VectorDistance queries."),
        new("cosmos-partitioning", "Cosmos DB partition design: choose a high-cardinality partition key, avoid hot partitions, and include tenant boundaries in query filters."),
        new("blob-artifacts", "Object store blobs hold immutable source payloads, extracted artifacts, manifests, and large binary files that should not live inside JSON records."),
        new("rag-context-assembly", "RAG context assembly selects retrieved chunks, citation spans, source labels, token budget limits, deduplication rules, and trace metadata before prompt construction."),
        new("rag-prompt-style", "Answer generation prompt style: use concise instructions, state uncertainty, avoid unsupported claims, and format final responses for the application."),
        new("service-plan-enrollment", "Service-plan enrollment: add eligible members, choose available plan options, confirm supporting records, and submit requested changes before the deadline."),
        new("travel-reimbursement", "Travel reimbursement policy: attach hotel folio, flight receipt, meal per diem details, business purpose, approval chain, and cost center."),
        new("record-collection-search", "HTTP client record search: query a collection with partition filters, metadata filters, vector search, hybrid lexical search, and continuation tokens."),
        new("provider-run-traces", "Provider run traces record model configuration, input hash, output hash, context references, latency, cost, cancellation status, and normalized failure class."),
        new("agent-memory-sources", "Agent memory recall returns prior notes with source references, provenance, semantic similarity scores, and freshness metadata for review."),
        new("semantic-kernel-connector", "Semantic Kernel connector mapping: translate Vyral records, vector fields, and metadata into Microsoft.Extensions.VectorData property shapes."),
        new("onnx-embedding-runtime", "Local ONNX embedding runtime uses quantized transformer models, CPU thread caps, optional CUDA-preferred execution, and clean fallback to CPU."),
        new("rag-ingest-manifest", "RAG ingest manifest records chunk ids, text hashes, source hashes, schema version, embedding configuration, vector reuse, and stale chunk cleanup decisions."),
        new("deadline-notice", "Deadline notice: an update was entered on March 12 with supporting source references and required follow-up dates."),
        new("source-record-log", "Source-record log lists reference ranges, source owners, classification labels, publication dates, and review notes.")
    };

    private static readonly QueryItem[] Queries =
    {
        new("after a retention hold ends, when can protected archives be deleted", "policy-data-retention"),
        new("equip a new teammate so they can sign in and work on day one", "onboarding-laptop"),
        new("confirm an AP bill matches the order and goods receipt before paying", "invoice-reconciliation"),
        new("stolen login token response steps for ending access quickly", "incident-compromised-credentials"),
        new("which Cosmos container policy sets vector path dimensions and distance", "cosmos-vector-policy"),
        new("deduplicate retrieved chunks, fit evidence into token budget, and attach source labels", "rag-context-assembly"),
        new("add eligible members while selecting annual service-plan options", "service-plan-enrollment"),
        new("expense a business trip with receipts and manager approval", "travel-reimbursement"),
        new("page through filtered semantic search results from a browser client", "record-collection-search"),
        new("recover prior agent notes and cite where they came from", "agent-memory-sources"),
        new("which ingest manifest lets unchanged chunk hashes reuse existing vectors", "rag-ingest-manifest"),
        new("limit local transformer inference threads when no accelerator is present", "onnx-embedding-runtime"),
        new("record LLM call cost, delay, cancellation, and error category", "provider-run-traces"),
        new("which deadline notice records a temporary restriction", "deadline-notice")
    };

    public static async Task<BenchmarkResult> RunAsync(BenchmarkOptions options)
    {
        using var provider = new OnnxTransformerEmbeddingProvider(new EmbeddingProviderOptions
        {
            Provider = options.Provider,
            ModelId = options.ModelId,
            ModelPath = options.ModelPath,
            Dimensions = options.Dimensions,
            ExecutionProvider = options.ExecutionProvider,
            MaxTokens = options.MaxTokens,
            Pooling = options.Pooling,
            IntraOpNumThreads = options.IntraOpNumThreads,
            InterOpNumThreads = options.InterOpNumThreads,
            CudaMemoryLimitMb = options.CudaMemoryLimitMb,
            QueryPrefix = options.QueryPrefix,
            PassagePrefix = options.PassagePrefix,
            SymmetricPrefix = options.SymmetricPrefix
        });

        var allTexts = Documents
            .Select(document => PrepareEmbeddingText(options, EmbeddingPurposes.Passage, document.Text))
            .Concat(Queries.Select(query => PrepareEmbeddingText(options, EmbeddingPurposes.Query, query.Text)))
            .ToArray();
        for (var i = 0; i < options.Warmup; i++)
        {
            await provider.GenerateEmbeddingAsync(allTexts[i % allTexts.Length]);
        }

        var timings = new List<double>(options.Iterations * allTexts.Length);
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wallClock = Stopwatch.StartNew();

        for (var iteration = 0; iteration < options.Iterations; iteration++)
        {
            foreach (var text in allTexts)
            {
                var sw = Stopwatch.StartNew();
                await provider.GenerateEmbeddingAsync(text);
                sw.Stop();
                timings.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        wallClock.Stop();
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;

        var retrieval = await MeasureRetrievalQuality(provider, options);
        timings.Sort();
        var wallClockMs = wallClock.Elapsed.TotalMilliseconds;
        var processCpuMs = (cpuAfter - cpuBefore).TotalMilliseconds;
        var logicalProcessorCount = Environment.ProcessorCount;
        var cpuCoreEquivalent = wallClockMs <= 0 ? 0 : processCpuMs / wallClockMs;
        var cpuUtilizationPercent = logicalProcessorCount <= 0 ? 0 : cpuCoreEquivalent / logicalProcessorCount * 100;

        return new BenchmarkResult
        {
            Provider = provider.ProviderId,
            ModelId = provider.ModelId,
            ModelPath = Path.GetFullPath(options.ModelPath),
            ActiveExecutionProvider = provider.ActiveExecutionProvider,
            FallbackReason = provider.ExecutionProviderFallbackReason,
            Dimensions = provider.Dimensions,
            MaxTokens = options.MaxTokens,
            Pooling = options.Pooling,
            IntraOpNumThreads = options.IntraOpNumThreads,
            InterOpNumThreads = options.InterOpNumThreads,
            CudaMemoryLimitMb = options.CudaMemoryLimitMb,
            QueryPrefix = options.QueryPrefix,
            PassagePrefix = options.PassagePrefix,
            SymmetricPrefix = options.SymmetricPrefix,
            Warmup = options.Warmup,
            Iterations = options.Iterations,
            EmbeddingCount = timings.Count,
            DocumentCount = Documents.Length,
            QueryCount = Queries.Length,
            WallClockMs = wallClockMs,
            ProcessCpuMs = processCpuMs,
            LogicalProcessorCount = logicalProcessorCount,
            CpuCoreEquivalent = cpuCoreEquivalent,
            CpuUtilizationPercent = cpuUtilizationPercent,
            MeanMs = timings.Average(),
            P50Ms = Percentile(timings, 0.50),
            P95Ms = Percentile(timings, 0.95),
            EmbeddingsPerSecond = timings.Count / wallClock.Elapsed.TotalSeconds,
            WorkingSetMb = process.WorkingSet64 / 1024.0 / 1024.0,
            HitAt1 = retrieval.HitAt1,
            HitAt3 = retrieval.HitAt3,
            MeanReciprocalRank = retrieval.MeanReciprocalRank,
            MeanExpectedScore = retrieval.MeanExpectedScore,
            MeanBestWrongScore = retrieval.MeanBestWrongScore,
            MeanScoreMargin = retrieval.MeanScoreMargin,
            WorstExpectedRank = retrieval.WorstExpectedRank,
            Misses = retrieval.Misses
        };
    }

    private static async Task<RetrievalMetrics> MeasureRetrievalQuality(OnnxTransformerEmbeddingProvider provider, BenchmarkOptions options)
    {
        var documentVectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var document in Documents)
        {
            documentVectors[document.Id] = await provider.GenerateEmbeddingAsync(PrepareEmbeddingText(options, EmbeddingPurposes.Passage, document.Text));
        }

        var hitAt1 = 0;
        var hitAt3 = 0;
        double reciprocalRank = 0;
        double expectedScoreTotal = 0;
        double bestWrongScoreTotal = 0;
        double scoreMarginTotal = 0;
        var worstExpectedRank = 0;
        var misses = new List<RetrievalMiss>();
        foreach (var query in Queries)
        {
            var queryVector = await provider.GenerateEmbeddingAsync(PrepareEmbeddingText(options, EmbeddingPurposes.Query, query.Text));
            var ranked = documentVectors
                .Select(item => new RankedItem(item.Key, Cosine(queryVector, item.Value)))
                .OrderByDescending(item => item.Score)
                .ToList();

            if (ranked[0].Id == query.ExpectedDocumentId)
            {
                hitAt1++;
            }

            var rank = ranked.FindIndex(item => item.Id == query.ExpectedDocumentId) + 1;
            if (rank <= 0)
            {
                throw new InvalidOperationException($"Expected document '{query.ExpectedDocumentId}' was not found in the benchmark corpus.");
            }

            if (rank <= 3)
            {
                hitAt3++;
            }

            reciprocalRank += 1.0 / rank;
            worstExpectedRank = Math.Max(worstExpectedRank, rank);

            var expectedScore = ranked[rank - 1].Score;
            var bestWrongScore = ranked.First(item => item.Id != query.ExpectedDocumentId).Score;
            expectedScoreTotal += expectedScore;
            bestWrongScoreTotal += bestWrongScore;
            scoreMarginTotal += expectedScore - bestWrongScore;

            if (rank > 1)
            {
                misses.Add(new RetrievalMiss(
                    query.Text,
                    query.ExpectedDocumentId,
                    rank,
                    expectedScore,
                    ranked[0].Id,
                    ranked[0].Score,
                    expectedScore - bestWrongScore,
                    ranked.Take(3).Select(item => item.Id).ToArray()));
            }
        }

        return new RetrievalMetrics(
            (double)hitAt1 / Queries.Length,
            (double)hitAt3 / Queries.Length,
            reciprocalRank / Queries.Length,
            expectedScoreTotal / Queries.Length,
            bestWrongScoreTotal / Queries.Length,
            scoreMarginTotal / Queries.Length,
            worstExpectedRank,
            misses);
    }

    private static string PrepareEmbeddingText(BenchmarkOptions options, string purpose, string text)
    {
        return EmbeddingTextPreparer.Prepare(
            text,
            purpose,
            options.QueryPrefix,
            options.PassagePrefix,
            options.SymmetricPrefix).PreparedText;
    }

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        var norm = Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm);
        return norm <= 0 ? 0 : dot / norm;
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile * values.Count) - 1;
        return values[Math.Clamp(index, 0, values.Count - 1)];
    }
}

internal sealed record WorkloadItem(string Id, string Text);

internal sealed record QueryItem(string Text, string ExpectedDocumentId);

internal sealed record RankedItem(string Id, double Score);

internal sealed record RetrievalMetrics(
    double HitAt1,
    double HitAt3,
    double MeanReciprocalRank,
    double MeanExpectedScore,
    double MeanBestWrongScore,
    double MeanScoreMargin,
    int WorstExpectedRank,
    IReadOnlyList<RetrievalMiss> Misses);

internal sealed record RetrievalMiss(
    string Query,
    string ExpectedDocumentId,
    int ExpectedRank,
    double ExpectedScore,
    string TopDocumentId,
    double TopScore,
    double ScoreMargin,
    IReadOnlyList<string> Top3DocumentIds);

internal sealed class BenchmarkResult
{
    public string Provider { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public string ActiveExecutionProvider { get; set; } = string.Empty;
    public string? FallbackReason { get; set; }
    public int Dimensions { get; set; }
    public int MaxTokens { get; set; }
    public string Pooling { get; set; } = string.Empty;
    public int? IntraOpNumThreads { get; set; }
    public int? InterOpNumThreads { get; set; }
    public long? CudaMemoryLimitMb { get; set; }
    public string? QueryPrefix { get; set; }
    public string? PassagePrefix { get; set; }
    public string? SymmetricPrefix { get; set; }
    public int Warmup { get; set; }
    public int Iterations { get; set; }
    public int EmbeddingCount { get; set; }
    public int DocumentCount { get; set; }
    public int QueryCount { get; set; }
    public double WallClockMs { get; set; }
    public double ProcessCpuMs { get; set; }
    public int LogicalProcessorCount { get; set; }
    public double CpuCoreEquivalent { get; set; }
    public double CpuUtilizationPercent { get; set; }
    public double MeanMs { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double EmbeddingsPerSecond { get; set; }
    public double WorkingSetMb { get; set; }
    public double HitAt1 { get; set; }
    public double HitAt3 { get; set; }
    public double MeanReciprocalRank { get; set; }
    public double MeanExpectedScore { get; set; }
    public double MeanBestWrongScore { get; set; }
    public double MeanScoreMargin { get; set; }
    public int WorstExpectedRank { get; set; }
    public IReadOnlyList<RetrievalMiss> Misses { get; set; } = Array.Empty<RetrievalMiss>();
}
