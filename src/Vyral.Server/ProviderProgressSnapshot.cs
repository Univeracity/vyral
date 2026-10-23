using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Providers.Abstractions;

namespace Vyral.Server;

internal sealed record ProviderProgressSnapshot(string JobId, int Attempt, string PayloadHash,
    string ProviderConfigHash, ProviderProgressObservation Observation)
{
    public static string? PhaseName(ProviderProgressPhase phase) => phase switch
    {
        ProviderProgressPhase.RuntimeBinding => "runtime_binding",
        ProviderProgressPhase.PromptPreparation => "prompt_preparation",
        ProviderProgressPhase.PromptPrepared => "prompt_prepared",
        ProviderProgressPhase.CompletionPending => "completion_pending",
        ProviderProgressPhase.CompletionValidation => "completion_validation",
        _ => null
    };

    public static bool Valid(ProviderProgressObservation value, DateTimeOffset now)
    {
        if (value is null || PhaseName(value.Phase) is null || value.Sequence <= 0
            || value.StartedAtUtc.Offset != TimeSpan.Zero || value.DeadlineUtc.Offset != TimeSpan.Zero
            || value.ObservedAtUtc.Offset != TimeSpan.Zero || value.DeadlineUtc <= value.StartedAtUtc
            || value.ObservedAtUtc < value.StartedAtUtc || value.ObservedAtUtc > value.DeadlineUtc
            || value.ObservedAtUtc > now) return false;
        if (value.Phase == ProviderProgressPhase.RuntimeBinding)
            return value.RuntimeContextTokens is null && value.ReservedOutputTokens is null
                && value.PromptTokens is null && value.PromptFitsContext is null;
        if (value.RuntimeContextTokens is not >= 2
            || value.ReservedOutputTokens is not >= 1
            || value.ReservedOutputTokens >= value.RuntimeContextTokens) return false;
        if (value.Phase == ProviderProgressPhase.PromptPreparation)
            return value.PromptTokens is null && value.PromptFitsContext is null;
        return value.PromptTokens >= 0 && value.PromptFitsContext ==
            (value.PromptTokens <= value.RuntimeContextTokens - value.ReservedOutputTokens);
    }

    public static JsonObject Project(ExecutionRun run, ProviderRunJob job, string? configHash, DateTimeOffset now)
    {
        ProviderProgressSnapshot? snapshot = null;
        try
        {
            snapshot = run.StatusDetails?["providerProgress"]?.Deserialize<ProviderProgressSnapshot>(ExecutionJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { }
        if (snapshot is not null && (snapshot.JobId != run.Id || snapshot.Attempt != run.Attempt
            || snapshot.PayloadHash != run.PayloadHash || snapshot.ProviderConfigHash != configHash
            || string.IsNullOrWhiteSpace(configHash)
            || !Valid(snapshot.Observation, now))) snapshot = null;
        var terminal = ExecutionRunStatuses.IsTerminal(run.Status);
        var invocation = ProviderInvocationJournal.Read(run, job.Provider);
        var reconciliation = run.StatusDetails?["providerRecovery"]?.ToString() == "reconciliation_required"
            || (run.StatusDetails?.ContainsKey("providerInvocation") == true && invocation is null)
            || invocation?.EffectsUnresolved == true || (terminal && invocation?.State == "intent");
        JsonObject? intent = invocation is null ? null : new JsonObject
        {
            ["state"] = invocation.State, ["originalAttempt"] = invocation.OriginalAttempt,
            ["startedAt"] = invocation.StartedAtUtc, ["deadlineUtc"] = invocation.DeadlineUtc,
            ["effectsUnresolved"] = invocation.EffectsUnresolved || (terminal && invocation.State == "intent")
        };
        var current = run.Status == ExecutionRunStatuses.Running && snapshot is not null
            && now < snapshot.Observation.DeadlineUtc
            && now - snapshot.Observation.ObservedAtUtc <= TimeSpan.FromSeconds(30);
        JsonObject? observation = null;
        if (snapshot is not null)
        {
            var item = snapshot.Observation;
            observation = new JsonObject
            {
                ["phase"] = PhaseName(item.Phase), ["attempt"] = snapshot.Attempt, ["sequence"] = item.Sequence,
                ["observedAt"] = item.ObservedAtUtc, ["startedAt"] = item.StartedAtUtc, ["deadlineUtc"] = item.DeadlineUtc,
                ["runtimeContextTokens"] = item.RuntimeContextTokens, ["promptTokens"] = item.PromptTokens,
                ["reservedOutputTokens"] = item.ReservedOutputTokens, ["promptFitsContext"] = item.PromptFitsContext,
                ["isCurrent"] = current
            };
        }
        return new JsonObject
        {
            ["id"] = job.Id, ["status"] = job.Status.ToString(),
            ["phase"] = terminal ? "terminal" : job.Status == ProviderJobStatus.Queued ? "queued"
                : current ? PhaseName(snapshot!.Observation.Phase) : "unknown",
            ["createdAt"] = job.CreatedAt, ["startedAt"] = job.StartedAt, ["completedAt"] = job.CompletedAt,
            ["cancellationRequested"] = job.CancellationRequested, ["nativePhaseAvailable"] = current,
            ["observation"] = observation, ["invocation"] = intent, ["reconciliationRequired"] = reconciliation
        };
    }
}
