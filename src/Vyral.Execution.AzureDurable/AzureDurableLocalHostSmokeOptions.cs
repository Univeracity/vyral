namespace Vyral.Execution.AzureDurable;

public sealed class AzureDurableLocalHostSmokeOptions
{
    public const string OptInEnvironmentVariableName = "VYRAL_EXECUTION_AZURE_LOCAL_HOST_SMOKE";
    public const string AzureWebJobsStorageSettingName = "AzureWebJobsStorage";
    public const string DurableTaskHubSettingName = "AzureFunctionsJobHost__extensions__durableTask__hubName";
    public const string FunctionsWorkerRuntimeSettingName = "FUNCTIONS_WORKER_RUNTIME";
    public const string LocalDevelopmentStorageConnectionString = "UseDevelopmentStorage=true";
    public const string DefaultTaskHubName = "VyralExecutionLocal";
    public const string DefaultWorkerRuntime = "dotnet-isolated";

    public bool Enabled { get; init; }
    public string StorageConnectionSettingName { get; init; } = AzureWebJobsStorageSettingName;
    public string StorageConnectionString { get; init; } = LocalDevelopmentStorageConnectionString;
    public string TaskHubName { get; init; } = DefaultTaskHubName;
    public string WorkerRuntime { get; init; } = DefaultWorkerRuntime;
    public string OptInSettingName { get; init; } = OptInEnvironmentVariableName;

    public IReadOnlyDictionary<string, string> BuildLocalSettings()
    {
        ValidateLocalOnly();

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorageConnectionSettingName.Trim()] = StorageConnectionString.Trim(),
            [FunctionsWorkerRuntimeSettingName] = WorkerRuntime.Trim(),
            [DurableTaskHubSettingName] = TaskHubName.Trim(),
            [OptInSettingName.Trim()] = Enabled ? "1" : "0"
        };
    }

    public void ValidateLocalOnly()
    {
        if (!string.Equals(
            NormalizeRequired(StorageConnectionSettingName, nameof(StorageConnectionSettingName)),
            AzureWebJobsStorageSettingName,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Azure local host smoke settings must use the default '{AzureWebJobsStorageSettingName}' setting name.");
        }

        if (!IsLocalDevelopmentStorage(StorageConnectionString))
        {
            throw new InvalidOperationException(
                "Azure local host smoke settings must use local development storage only.");
        }

        var taskHubName = NormalizeRequired(TaskHubName, nameof(TaskHubName));
        if (!string.Equals(taskHubName, DefaultTaskHubName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Azure local host smoke settings must use the default '{DefaultTaskHubName}' task hub name.");
        }

        if (!string.Equals(
            NormalizeRequired(WorkerRuntime, nameof(WorkerRuntime)),
            DefaultWorkerRuntime,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Azure local host smoke settings must use the default '{DefaultWorkerRuntime}' worker runtime.");
        }

        if (!string.Equals(
            NormalizeRequired(OptInSettingName, nameof(OptInSettingName)),
            OptInEnvironmentVariableName,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Azure local host smoke settings must use the default '{OptInEnvironmentVariableName}' opt-in setting name.");
        }
    }

    public static bool IsLocalDevelopmentStorage(string? storageConnectionString)
    {
        return string.Equals(
            storageConnectionString?.Trim(),
            LocalDevelopmentStorageConnectionString,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return value.Trim();
    }
}
