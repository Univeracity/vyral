using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vyral.Execution;

namespace Vyral.Execution.Temporal;

public sealed class TemporalExecutionOptions
{
    private static readonly Regex AdapterNamespacePattern = new(
        "^[a-z0-9][a-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ObjectContainerPattern = new(
        "^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public string AdapterId { get; init; } = "temporal";
    public string AdapterNamespace { get; init; } = "default";
    public string TargetHost { get; init; } = string.Empty;
    public string Namespace { get; init; } = "default";
    public string TaskQueue { get; init; } = "vyral-execution";
    public string WorkerDeploymentName { get; init; } = "vyral-execution";
    public string? WorkerBuildId { get; init; }
    public string ArtifactObjectContainer { get; init; } = "vyral-execution";
    public bool RequireTls { get; init; } = true;
    public int ReconciliationBatchSize { get; init; } = 100;
    public int DefaultListLimit { get; init; } = 100;
    public int MaxListLimit { get; init; } = 1_000;
    public int DefaultHistoryLimit { get; init; } = 100;
    public int MaxHistoryLimit { get; init; } = 1_000;
    public ExecutionRuntimeLimits Limits { get; init; } = ExecutionRuntimeLimits.Default;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Limits);
        if (Limits.MaxIdChars < 1)
            throw new InvalidOperationException("Execution runtime identifier limit must be positive.");
        ValidateBoundedSetting(AdapterId, "Temporal adapter id", Limits.MaxIdChars);

        if (!AdapterNamespacePattern.IsMatch(AdapterNamespace))
        {
            throw new InvalidOperationException("Temporal adapter namespace must be 1-64 lowercase portable identifier characters.");
        }
        ValidateBoundedSetting(Namespace, "Temporal namespace", 255);
        ValidateBoundedSetting(TaskQueue, "Temporal task queue", 255);
        ValidateBoundedSetting(WorkerDeploymentName, "Temporal worker deployment name", 255);
        if (WorkerDeploymentName.Contains('.', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Temporal worker deployment name cannot contain a period.");
        }
        if (WorkerBuildId is not null)
        {
            ValidateBoundedSetting(WorkerBuildId, "Temporal worker build id", 255);
        }
        if (!ObjectContainerPattern.IsMatch(ArtifactObjectContainer) ||
            ArtifactObjectContainer.Contains("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Temporal artifact object container must be 3-63 lowercase letters, digits, or single hyphens and must start and end with a letter or digit.");
        }
        var target = ParseTargetHost(TargetHost);
        if (!RequireTls && !IsLoopback(target.Host))
        {
            throw new InvalidOperationException("Temporal TLS can be disabled only for a loopback development target.");
        }
        if (ReconciliationBatchSize is < 1 or > 1_000)
        {
            throw new InvalidOperationException("Temporal reconciliation batch size must be between 1 and 1000.");
        }
        if (DefaultListLimit < 1 || MaxListLimit < DefaultListLimit || MaxListLimit > 1_000)
            throw new InvalidOperationException("Temporal run list limits must be positive, ordered, and at most 1000.");
        if (DefaultHistoryLimit < 1 || MaxHistoryLimit < DefaultHistoryLimit || MaxHistoryLimit > 1_000)
            throw new InvalidOperationException("Temporal history limits must be positive, ordered, and at most 1000.");
    }

    public IReadOnlyDictionary<string, string> ToDiagnosticMetadata()
    {
        Validate();
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["adapterNamespace"] = AdapterNamespace,
            ["targetHostHash"] = HashForDisplay(TargetHost),
            ["namespaceHash"] = HashForDisplay(Namespace),
            ["taskQueueHash"] = HashForDisplay(TaskQueue),
            ["workerDeploymentNameHash"] = HashForDisplay(WorkerDeploymentName),
            ["artifactObjectContainerHash"] = HashForDisplay(ArtifactObjectContainer),
            ["tlsRequired"] = RequireTls ? "true" : "false",
            ["reconciliationBatchSize"] = ReconciliationBatchSize.ToString(CultureInfo.InvariantCulture),
            ["defaultListLimit"] = DefaultListLimit.ToString(CultureInfo.InvariantCulture),
            ["maxListLimit"] = MaxListLimit.ToString(CultureInfo.InvariantCulture),
            ["defaultHistoryLimit"] = DefaultHistoryLimit.ToString(CultureInfo.InvariantCulture),
            ["maxHistoryLimit"] = MaxHistoryLimit.ToString(CultureInfo.InvariantCulture)
        };
    }

    public static TemporalExecutionOptions FromFactoryContext(ExecutionRuntimeAdapterFactoryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = new Dictionary<string, string?>(context.Settings, StringComparer.OrdinalIgnoreCase);
        var options = new TemporalExecutionOptions
        {
            AdapterId = FirstNonEmpty(settings, "ExecutionRuntime:AdapterId") ?? context.AdapterId,
            AdapterNamespace = FirstNonEmpty(settings, "ExecutionRuntime:Temporal:AdapterNamespace", "VYRAL_EXECUTION_TEMPORAL_ADAPTER_NAMESPACE") ?? "default",
            TargetHost = FirstNonEmpty(settings, "ExecutionRuntime:Temporal:TargetHost", "VYRAL_EXECUTION_TEMPORAL_TARGET_HOST") ?? string.Empty,
            Namespace = FirstNonEmpty(settings, "ExecutionRuntime:Temporal:Namespace", "VYRAL_EXECUTION_TEMPORAL_NAMESPACE") ?? "default",
            TaskQueue = FirstNonEmpty(settings, "ExecutionRuntime:Temporal:TaskQueue", "VYRAL_EXECUTION_TEMPORAL_TASK_QUEUE") ?? "vyral-execution",
            WorkerDeploymentName = FirstNonEmpty(
                settings,
                "ExecutionRuntime:Temporal:WorkerDeploymentName",
                "VYRAL_EXECUTION_TEMPORAL_WORKER_DEPLOYMENT_NAME") ?? "vyral-execution",
            WorkerBuildId = FirstNonEmpty(
                settings,
                "ExecutionRuntime:Temporal:WorkerBuildId",
                "VYRAL_EXECUTION_TEMPORAL_WORKER_BUILD_ID"),
            ArtifactObjectContainer = FirstNonEmpty(
                settings,
                "ExecutionRuntime:Temporal:ArtifactObjectContainer",
                "VYRAL_EXECUTION_TEMPORAL_ARTIFACT_OBJECT_CONTAINER") ?? "vyral-execution",
            RequireTls = ParseBoolean(settings, true, "ExecutionRuntime:Temporal:RequireTls", "VYRAL_EXECUTION_TEMPORAL_REQUIRE_TLS"),
            ReconciliationBatchSize = ParseInt(settings, 100, "ExecutionRuntime:Temporal:ReconciliationBatchSize", "VYRAL_EXECUTION_TEMPORAL_RECONCILIATION_BATCH_SIZE"),
            DefaultListLimit = ParseInt(settings, 100, "ExecutionRuntime:Temporal:DefaultListLimit"),
            MaxListLimit = ParseInt(settings, 1_000, "ExecutionRuntime:Temporal:MaxListLimit"),
            DefaultHistoryLimit = ParseInt(settings, 100, "ExecutionRuntime:Temporal:DefaultHistoryLimit"),
            MaxHistoryLimit = ParseInt(settings, 1_000, "ExecutionRuntime:Temporal:MaxHistoryLimit")
        };
        options.Validate();
        return options;
    }

    internal static Uri ParseTargetHost(string targetHost)
    {
        if (string.IsNullOrWhiteSpace(targetHost) ||
            targetHost.Contains("://", StringComparison.Ordinal) ||
            targetHost.IndexOfAny(['/', '?', '#', '@']) >= 0 ||
            !Uri.TryCreate($"tcp://{targetHost.Trim()}", UriKind.Absolute, out var target) ||
            string.IsNullOrWhiteSpace(target.Host) || target.Port is < 1 or > 65_535)
        {
            throw new InvalidOperationException("Temporal target host must be a host:port value without a scheme, path, query, or credentials.");
        }
        return target;
    }

    internal static string HashForDisplay(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())))[..16].ToLowerInvariant();

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);

    private static void ValidateBoundedSetting(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"{name} must be 1-{maximumLength} non-control characters.");
        }
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string?> settings, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        return null;
    }

    private static bool ParseBoolean(IReadOnlyDictionary<string, string?> settings, bool fallback, params string[] keys)
    {
        var value = FirstNonEmpty(settings, keys);
        if (value is null) return fallback;
        if (bool.TryParse(value, out var parsed)) return parsed;
        throw new InvalidOperationException($"Temporal setting '{keys[0]}' must be true or false.");
    }

    private static int ParseInt(IReadOnlyDictionary<string, string?> settings, int fallback, params string[] keys)
    {
        var value = FirstNonEmpty(settings, keys);
        if (value is null) return fallback;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new InvalidOperationException($"Temporal setting '{keys[0]}' must be an integer.");
    }
}

public static class TemporalExecutionRuntimeKindIds
{
    public const string Temporal = "temporal.workflow";
}

public static class TemporalExecutionIdentity
{
    private static readonly Regex PortableIdentityPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string CreateWorkflowId(string adapterNamespace, string runId)
    {
        if (string.IsNullOrWhiteSpace(adapterNamespace) ||
            !Regex.IsMatch(adapterNamespace, "^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
        {
            throw new InvalidOperationException("Temporal adapter namespace is not portable.");
        }
        if (string.IsNullOrWhiteSpace(runId) || !PortableIdentityPattern.IsMatch(runId))
        {
            throw new InvalidOperationException("Vyral run id is not safe for a Temporal workflow identity.");
        }
        return $"vyral:{adapterNamespace}:{runId}";
    }
}
