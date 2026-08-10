using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vyral.Azure;
using Vyral.Execution;
using Vyral.Execution.AzureDurable;
using Vyral.Execution.AzureDurableFunctionsSmoke;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

var configuration = builder.Configuration;
var cosmosConnectionString = Required(configuration["VYRAL_AZURE_COSMOS_CONNECTION_STRING"], "VYRAL_AZURE_COSMOS_CONNECTION_STRING");
var cosmosDatabase = Required(configuration["VYRAL_AZURE_COSMOS_DATABASE"], "VYRAL_AZURE_COSMOS_DATABASE");
var statusContainer = Required(configuration["VYRAL_AZURE_DURABLE_STATUS_CONTAINER"], "VYRAL_AZURE_DURABLE_STATUS_CONTAINER");

var options = new AzureDurableExecutionOptions
{
    AdapterId = "azure-durable-smoke",
    TaskHubName = Required(configuration["VYRAL_AZURE_DURABLE_TASK_HUB"], "VYRAL_AZURE_DURABLE_TASK_HUB"),
    StatusStoreName = statusContainer,
    WorkerId = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? Environment.MachineName
};

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new CosmosClient(cosmosConnectionString));
builder.Services.AddSingleton<AzureCosmosExecutionStatusStore>(serviceProvider =>
    new AzureCosmosExecutionStatusStore(
        serviceProvider.GetRequiredService<CosmosClient>(),
        cosmosDatabase,
        statusContainer,
        serviceProvider.GetRequiredService<AzureDurableExecutionOptions>()));
builder.Services.AddSingleton<AzureDurableExecutionHost>(serviceProvider =>
{
    var host = new AzureDurableExecutionHost(
        serviceProvider.GetRequiredService<AzureDurableExecutionOptions>(),
        store: serviceProvider.GetRequiredService<AzureCosmosExecutionStatusStore>());
    host.RegisterHandler(new DelegateExecutionHandler(
        new ExecutionHandlerDescriptor
        {
            HandlerId = AzureDurableSmokeHandler.HandlerId,
            PluginId = AzureDurableSmokeHandler.PluginId,
            DisplayName = "Azure Durable Functions smoke handler",
            Description = "Writes a checkpoint, artifact, and trace before succeeding."
        },
        AzureDurableSmokeHandler.ExecuteAsync));
    return host;
});

builder.Build().Run();

static string Required(string? value, string name) =>
    string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"Configuration value '{name}' is required.")
        : value.Trim();
