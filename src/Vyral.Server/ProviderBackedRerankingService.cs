using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Microsoft.Extensions.Configuration;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Local;

namespace Vyral.Server;

public sealed class ProviderBackedRerankingService : IRerankingService
{
    private readonly ProviderTargetRegistry _registry;
    private readonly ProviderRunGuard _guard;
    private readonly ITraceStore _traces;
    private readonly IConfiguration _configuration;

    public ProviderBackedRerankingService(
        ProviderTargetRegistry registry,
        ProviderRunGuard guard,
        ITraceStore traces,
        IConfiguration configuration)
    {
        _registry = registry;
        _guard = guard;
        _traces = traces;
        _configuration = configuration;
    }

    public async Task<RerankResult> RerankAsync(RerankRequest request, CancellationToken ct = default)
    {
        var options = request.Options;
        var providerId = string.IsNullOrWhiteSpace(options.Provider)
            ? _configuration["Retrieval:Rerank:Provider"] ?? LocalTokenOverlapRerankerProviderTarget.ProviderId
            : options.Provider.Trim();
        var target = _registry.GetTarget(providerId);
        if (target is null)
        {
            throw new InvalidOperationException($"Rerank provider '{providerId}' is not configured.");
        }

        if (!target.Capabilities.Any(c => string.Equals(c.Id, ProviderCapabilityIds.AiRerank, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Provider '{providerId}' does not support {ProviderCapabilityIds.AiRerank}.");
        }

        var mode = string.IsNullOrWhiteSpace(options.Mode) ? "advisory" : options.Mode;
        var payload = ProviderJson.ToJsonObject(new AiRerankRequest
        {
            Query = request.Query,
            Limit = request.Limit,
            Candidates = request.Candidates.Select(candidate => new AiRerankCandidate
            {
                Id = candidate.Id,
                Text = candidate.Text,
                Metadata = BuildProviderCandidateMetadata(candidate)
            }).ToList()
        });
        var payloadBytes = Encoding.UTF8.GetByteCount(payload.ToJsonString(ProviderJson.Options));
        var maxInputBytes = ResolveMaxInputBytes(target, ProviderCapabilityIds.AiRerank, mode);

        var providerRequest = new ProviderRunRequest
        {
            Provider = providerId,
            Capability = ProviderCapabilityIds.AiRerank,
            Operation = "run",
            Mode = mode,
            TimeoutSeconds = options.TimeoutSeconds,
            MaxOutputBytes = options.MaxOutputBytes,
            ArtifactDirectory = GetProviderArtifactDirectory(),
            ContextRefs = request.Candidates
                .Select(candidate => $"record:{candidate.Collection}/{candidate.PartitionKey}/{candidate.RecordId}")
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            Payload = payload
        };

        var providerResult = await RunWithGuardAsync(target, providerRequest, providerId, ct);
        await PersistProviderTraceAsync(providerRequest, providerResult, payloadBytes, maxInputBytes, ct);
        var traceEvent = providerResult.Trace;

        if (providerResult.Status != ProviderRunStatus.Succeeded)
        {
            throw new RerankProviderException(
                $"Rerank provider '{providerId}' returned {providerResult.Status}: {providerResult.ProviderStatus ?? providerResult.FailureClass ?? "unknown"}.",
                providerId,
                providerResult.Status.ToString(),
                providerResult.FailureClass,
                providerResult.ProviderStatus,
                traceEvent?.TraceId,
                payloadBytes,
                maxInputBytes,
                request.Candidates.Count);
        }

        var rerank = providerResult.Output.Deserialize<AiRerankResult>(ProviderJson.Options)
            ?? throw new InvalidOperationException($"Rerank provider '{providerId}' returned an invalid rerank result.");

        return new RerankResult
        {
            Provider = providerId,
            ModelId = traceEvent?.ModelId,
            TraceId = traceEvent?.TraceId,
            InputCandidateCount = request.Candidates.Count,
            ProviderPayloadBytes = payloadBytes,
            ProviderMaxInputBytes = maxInputBytes,
            Items = rerank.Items.Select(item => new RerankResultItem
            {
                Id = item.Id,
                Rank = item.Rank,
                Score = (float)item.Score
            }).ToList()
        };
    }

    private async Task<ProviderRunResult> RunWithGuardAsync(IProviderTarget target, ProviderRunRequest request, string providerId, CancellationToken ct)
    {
        await using var admission = await _guard.TryEnterAsync(providerId, request, ct);
        if (!admission.Accepted)
        {
            return admission.RejectionResult!;
        }

        try
        {
            return await target.RunAsync(request, admission.CancellationToken);
        }
        catch (OperationCanceledException) when (admission.TimedOut)
        {
            return _guard.CreateTimeoutResult(providerId, request);
        }
    }

    private async Task PersistProviderTraceAsync(
        ProviderRunRequest request,
        ProviderRunResult result,
        int payloadBytes,
        int? maxInputBytes,
        CancellationToken ct)
    {
        var traceEvent = result.Trace ??= new ProviderTraceEvent
        {
            Provider = result.Provider,
            Capability = result.Capability,
            Operation = result.Operation,
            Mode = result.Mode,
            InputHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options)),
            FailureClass = result.FailureClass
        };

        if (string.IsNullOrWhiteSpace(traceEvent.TraceId))
        {
            traceEvent.TraceId = Guid.NewGuid().ToString("N");
        }

        var trace = new TraceRecord
        {
            Id = traceEvent.TraceId,
            Operation = "provider.run",
            Adapter = result.Provider,
            StartedAt = traceEvent.Timestamp == default ? DateTime.UtcNow : traceEvent.Timestamp,
            DurationMs = traceEvent.DurationMs,
            Request = new Dictionary<string, object?>
            {
                ["provider"] = result.Provider,
                ["capability"] = request.Capability,
                ["operation"] = request.Operation,
                ["mode"] = request.Mode,
                ["correlationId"] = request.CorrelationId,
                ["contextRefs"] = request.ContextRefs,
                ["timeoutSeconds"] = request.TimeoutSeconds,
                ["maxOutputBytes"] = request.MaxOutputBytes,
                ["payloadBytes"] = payloadBytes,
                ["maxInputBytes"] = maxInputBytes,
                ["payloadHash"] = traceEvent.InputHash ?? ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options))
            },
            ResultSummary = new Dictionary<string, object?>
            {
                ["status"] = result.Status.ToString(),
                ["failureClass"] = result.FailureClass,
                ["providerStatus"] = result.ProviderStatus,
                ["inputHash"] = traceEvent.InputHash,
                ["outputHash"] = traceEvent.OutputHash,
                ["modelId"] = traceEvent.ModelId,
                ["adapterId"] = traceEvent.AdapterId,
                ["configHash"] = traceEvent.ConfigHash,
                ["authorityBoundary"] = traceEvent.AuthorityBoundary,
                ["artifactRefs"] = traceEvent.ArtifactRefs
            }
        };

        await _traces.WriteTraceAsync(trace, ct);
    }

    private string? GetProviderArtifactDirectory()
    {
        var value = _configuration["Providers:ArtifactDirectory"];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static JsonObject BuildProviderCandidateMetadata(RerankCandidate candidate)
    {
        var metadata = new JsonObject
        {
            ["collection"] = candidate.Collection,
            ["partitionKey"] = candidate.PartitionKey,
            ["recordId"] = candidate.RecordId,
            ["originalRank"] = candidate.OriginalRank,
            ["originalScore"] = candidate.OriginalScore
        };

        if (candidate.Metadata?["type"] is JsonValue typeNode &&
            typeNode.TryGetValue<string>(out var type) &&
            !string.IsNullOrWhiteSpace(type))
        {
            metadata["type"] = type;
        }

        return metadata;
    }

    private static int? ResolveMaxInputBytes(IProviderTarget target, string capabilityId, string mode)
    {
        var capability = target.Capabilities.FirstOrDefault(item => string.Equals(item.Id, capabilityId, StringComparison.OrdinalIgnoreCase));
        var policy = capability is null ? null : ProviderModePolicies.Resolve(ProviderModePolicies.Index(capability.ModePolicies), mode);
        return policy?.MaxInputBytes;
    }
}
