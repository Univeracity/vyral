using System.Text;

namespace Vyral.Tests.Azure;

public sealed class AzureBlobLiveFactAttribute : FactAttribute
{
    public AzureBlobLiveFactAttribute()
    {
        if (!AzureLiveSettings.HasBlobSettings)
        {
            Skip = "Set VYRAL_AZURE_BLOB_CONNECTION_STRING to run Azure Blob live tests.";
        }
    }
}

public sealed class AzureCosmosLiveFactAttribute : FactAttribute
{
    public AzureCosmosLiveFactAttribute()
    {
        if (!AzureLiveSettings.HasCosmosSettings)
        {
            Skip = "Set VYRAL_AZURE_COSMOS_CONNECTION_STRING and VYRAL_AZURE_COSMOS_DATABASE to run Cosmos DB live tests.";
        }
    }
}

/// <summary>
/// Opt-in assertion for a run produced by the deployed Azure Durable Functions smoke host.
/// The test never starts, modifies, or deletes a Function App; it only reads its Cosmos state.
/// </summary>
public sealed class AzureDurableFunctionsSmokeLiveFactAttribute : FactAttribute
{
    public AzureDurableFunctionsSmokeLiveFactAttribute()
    {
        if (!AzureLiveSettings.HasCosmosSettings ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AZURE_DURABLE_STATUS_CONTAINER")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AZURE_DURABLE_SMOKE_RUN_ID")))
        {
            Skip = "Set Cosmos settings plus VYRAL_AZURE_DURABLE_STATUS_CONTAINER and VYRAL_AZURE_DURABLE_SMOKE_RUN_ID to verify a deployed Durable Functions smoke run.";
        }
    }
}

/// <summary>
/// Opt-in assertion for a deployed smoke run that exercised a portable durable wait. Like the
/// ordinary smoke assertion, this is read-only and receives the disposable resource identifiers
/// from the deployment gate.
/// </summary>
public sealed class AzureDurableFunctionsSmokeWaitLiveFactAttribute : FactAttribute
{
    public AzureDurableFunctionsSmokeWaitLiveFactAttribute()
    {
        if (!AzureLiveSettings.HasCosmosSettings ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AZURE_DURABLE_STATUS_CONTAINER")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AZURE_DURABLE_SMOKE_WAIT_RUN_ID")))
        {
            Skip = "Set Cosmos settings plus VYRAL_AZURE_DURABLE_STATUS_CONTAINER and VYRAL_AZURE_DURABLE_SMOKE_WAIT_RUN_ID to verify a deployed Durable Functions durable-wait smoke run.";
        }
    }
}

/// <summary>
/// Opt-in assertion for a deployed smoke run that exercised a portable durable timer wait.
/// Like the other smoke assertions, it is read-only and receives the disposable resource
/// identifiers from the deployment gate.
/// </summary>
public sealed class AzureDurableFunctionsSmokeTimerLiveFactAttribute : FactAttribute
{
    public AzureDurableFunctionsSmokeTimerLiveFactAttribute()
    {
        if (!AzureLiveSettings.HasCosmosSettings ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AZURE_DURABLE_STATUS_CONTAINER")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AZURE_DURABLE_SMOKE_TIMER_RUN_ID")))
        {
            Skip = "Set Cosmos settings plus VYRAL_AZURE_DURABLE_STATUS_CONTAINER and VYRAL_AZURE_DURABLE_SMOKE_TIMER_RUN_ID to verify a deployed Durable Functions timer-wait smoke run.";
        }
    }
}

internal static class AzureLiveSettings
{
    public static bool HasBlobSettings =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AZURE_BLOB_CONNECTION_STRING"));

    public static bool HasCosmosSettings =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AZURE_COSMOS_CONNECTION_STRING")) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VYRAL_AZURE_COSMOS_DATABASE"));

    public static BlobLiveSettings Blob()
    {
        return new BlobLiveSettings(
            Require("VYRAL_AZURE_BLOB_CONNECTION_STRING"),
            Environment.GetEnvironmentVariable("VYRAL_AZURE_BLOB_CONTAINER_PREFIX") ?? "vyral-test");
    }

    public static CosmosLiveSettings Cosmos()
    {
        return new CosmosLiveSettings(
            Require("VYRAL_AZURE_COSMOS_CONNECTION_STRING"),
            Require("VYRAL_AZURE_COSMOS_DATABASE"),
            Environment.GetEnvironmentVariable("VYRAL_AZURE_COSMOS_CONTAINER_PREFIX") ?? "vyral-test");
    }

    public static string UniqueContainerName(string prefix)
    {
        var normalized = NormalizeAzureName(prefix);
        var suffix = Guid.NewGuid().ToString("N")[..16];
        var maxPrefixLength = 63 - suffix.Length - 1;
        if (normalized.Length > maxPrefixLength)
        {
            normalized = normalized[..maxPrefixLength].Trim('-');
        }

        if (normalized.Length < 3)
        {
            normalized = "vyral";
        }

        return $"{normalized}-{suffix}";
    }

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return value;
    }

    private static string NormalizeAzureName(string value)
    {
        var builder = new StringBuilder();
        var previousHyphen = false;
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                previousHyphen = false;
            }
            else if (!previousHyphen && builder.Length > 0)
            {
                builder.Append('-');
                previousHyphen = true;
            }
        }

        return builder.ToString().Trim('-');
    }
}

internal sealed record BlobLiveSettings(string ConnectionString, string ContainerPrefix);

internal sealed record CosmosLiveSettings(string ConnectionString, string DatabaseId, string ContainerPrefix);
