using System.Text.Json.Nodes;
using Vyral.Execution.AzureDurable;

namespace Vyral.Tests.Azure;

public sealed class AzureDurableExecutionLocalHostSmokeTests
{
    [Fact]
    public void AzureDurableExecution_LocalHostSmokeDefaultsAreOptInAndLocalOnly()
    {
        var options = new AzureDurableLocalHostSmokeOptions();

        var settings = options.BuildLocalSettings();

        Assert.False(options.Enabled);
        Assert.Equal(
            AzureDurableLocalHostSmokeOptions.LocalDevelopmentStorageConnectionString,
            settings[AzureDurableLocalHostSmokeOptions.AzureWebJobsStorageSettingName]);
        Assert.Equal(
            AzureDurableLocalHostSmokeOptions.DefaultWorkerRuntime,
            settings[AzureDurableLocalHostSmokeOptions.FunctionsWorkerRuntimeSettingName]);
        Assert.Equal(
            AzureDurableLocalHostSmokeOptions.DefaultTaskHubName,
            settings[AzureDurableLocalHostSmokeOptions.DurableTaskHubSettingName]);
        Assert.Equal("0", settings[AzureDurableLocalHostSmokeOptions.OptInEnvironmentVariableName]);
        Assert.DoesNotContain(settings.Values, LooksLikeCloudStorageConnection);
    }

    [Fact]
    public void AzureDurableExecution_LocalHostSmokeCanEmitExplicitOptInSetting()
    {
        var options = new AzureDurableLocalHostSmokeOptions { Enabled = true };

        var settings = options.BuildLocalSettings();

        Assert.Equal("1", settings[AzureDurableLocalHostSmokeOptions.OptInEnvironmentVariableName]);
    }

    [Theory]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=prod;AccountKey=secret")]
    [InlineData("AccountName=prod;AccountKey=secret")]
    [InlineData("BlobEndpoint=https://storage.example.invalid/container")]
    [InlineData("")]
    [InlineData(" ")]
    public void AzureDurableExecution_LocalHostSmokeRejectsNonLocalStorage(string storageConnectionString)
    {
        var options = new AzureDurableLocalHostSmokeOptions
        {
            StorageConnectionString = storageConnectionString
        };

        var error = Assert.Throws<InvalidOperationException>(() => options.BuildLocalSettings());
        Assert.Contains("local development storage", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ProdHub")]
    [InlineData("prod-hub")]
    [InlineData("1ProdHub")]
    [InlineData("ab")]
    public void AzureDurableExecution_LocalHostSmokeRejectsNonDefaultTaskHubNames(string taskHubName)
    {
        var options = new AzureDurableLocalHostSmokeOptions
        {
            TaskHubName = taskHubName
        };

        var error = Assert.Throws<InvalidOperationException>(() => options.BuildLocalSettings());
        Assert.Contains("task hub", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AzureDurableExecution_LocalHostSmokeRejectsRenamedResourceSettings()
    {
        var options = new AzureDurableLocalHostSmokeOptions
        {
            StorageConnectionSettingName = "RemoteStorage"
        };

        var error = Assert.Throws<InvalidOperationException>(() => options.BuildLocalSettings());
        Assert.Contains("default", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AzureDurableExecution_LocalHostSkeletonFilesMirrorLocalOnlyDefaults()
    {
        var expected = new AzureDurableLocalHostSmokeOptions().BuildLocalSettings();
        var settingsPath = FindRepoFile("samples/Vyral.Execution.AzureDurableLocalHost/local.settings.sample.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject()["Values"]!.AsObject();

        Assert.Equal(expected.Count, settings.Count);
        foreach (var (key, value) in expected)
        {
            Assert.True(settings.TryGetPropertyValue(key, out var node), $"Missing setting '{key}'.");
            Assert.Equal(value, node!.GetValue<string>());
        }

        Assert.All(settings.Select(item => item.Value!.GetValue<string>()), value =>
            Assert.False(LooksLikeCloudStorageConnection(value), $"Setting value must stay local-only: {value}"));

        var hostPath = FindRepoFile("samples/Vyral.Execution.AzureDurableLocalHost/host.json");
        var host = JsonNode.Parse(File.ReadAllText(hostPath))!.AsObject();
        var hubName = host["extensions"]!["durableTask"]!["hubName"]!.GetValue<string>();
        Assert.Equal(AzureDurableLocalHostSmokeOptions.DefaultTaskHubName, hubName);
        Assert.False(LooksLikeCloudStorageConnection(hubName));
    }

    private static bool LooksLikeCloudStorageConnection(string value)
    {
        return value.Contains("AccountName=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("AccountKey=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("DefaultEndpointsProtocol=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("BlobEndpoint=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("QueueEndpoint=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("TableEndpoint=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }
}
