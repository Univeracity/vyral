using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Execution;
using Vyral.Providers.Abstractions;

namespace Vyral.Server;

/// <summary>Durable dispatch intent and atomic completion, scoped to one admitted job.</summary>
internal sealed record ProviderInvocationJournal(string SchemaVersion, string JobId, int OriginalAttempt,
    string PayloadHash, string Provider, string? ConfigHash, DateTimeOffset StartedAtUtc,
    DateTimeOffset DeadlineUtc, string State, string? ResultHash = null, bool EffectsUnresolved = false)
{
    public const string Version = "vyral.provider-invocation.v1";

    public static ProviderInvocationJournal? Read(ExecutionRun run, string provider)
    {
        ProviderInvocationJournal? value;
        try { value = run.StatusDetails?["providerInvocation"]?.Deserialize<ProviderInvocationJournal>(ExecutionJson.Options); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { return null; }
        if (value is null || value.SchemaVersion != Version || value.JobId != run.Id
            || value.PayloadHash != run.PayloadHash || !string.Equals(value.Provider, provider, StringComparison.OrdinalIgnoreCase)
            || value.OriginalAttempt < 0 || value.OriginalAttempt > run.Attempt
            || value.StartedAtUtc.Offset != TimeSpan.Zero || value.DeadlineUtc.Offset != TimeSpan.Zero
            || value.StartedAtUtc >= value.DeadlineUtc || value.State is not ("intent" or "completed")
            || (value.State == "completed" && string.IsNullOrWhiteSpace(value.ResultHash))) return null;
        return value;
    }

    public ProviderRunResult? RetainedCompletion(ExecutionRun run, ProviderRunRequest request)
    {
        if (State != "completed" || run.Result is null || string.IsNullOrEmpty(ResultHash)
            || ResultHash != ProviderHash.Sha256(run.Result.ToJsonString(ExecutionJson.Options))) return null;
        try
        {
            var result = run.Result.Deserialize<ProviderRunResult>(ExecutionJson.Options);
            return result is not null && Enum.IsDefined(result.Status)
                && string.Equals(result.Provider, request.Provider, StringComparison.OrdinalIgnoreCase)
                && result.Capability == request.Capability && result.Operation == request.Operation && result.Mode == request.Mode
                ? result : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { return null; }
    }
}
