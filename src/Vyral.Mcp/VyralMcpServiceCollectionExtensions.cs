using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Vyral.Mcp;

public static class VyralMcpServiceCollectionExtensions
{
    public static IMcpServerBuilder AddVyralMcp(
        this IServiceCollection services,
        VyralMcpOptions options,
        IMcpTaskStore? taskStore = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddHttpContextAccessor();

        var builder = services.AddMcpServer(server =>
            {
                server.ProtocolVersion = VyralMcpOptions.ProtocolVersion;
                server.ServerInfo = new Implementation
                {
                    Name = "vyral",
                    Version = "0.3.0"
                };
                server.ServerInstructions =
                    "Use Vyral tools for bounded record, retrieval, RAG, graph, and durable execution reads. " +
                    "Use the REST SDKs for binary artifact transfer. Every request is stateless and independently authorized.";
            })
            .WithHttpTransport(transport =>
            {
                transport.Stateless = true;
                transport.ConfigureSessionOptions = (_, serverOptions, _) =>
                {
                    var tools = serverOptions.ToolCollection;
                    foreach (var tool in tools?.ToArray() ?? [])
                    {
                        if (!VyralMcpCatalog.TryGetByMcpId(tool.ProtocolTool.Name, out var catalog))
                        {
                            if (!options.ConformanceMode) tools!.Remove(tool);
                            continue;
                        }
                        if (!VyralMcpCatalog.IsEnabled(catalog, options))
                            tools!.Remove(tool);
                    }
                    var resources = serverOptions.ResourceCollection;
                    foreach (var resource in resources?.ToArray() ?? [])
                    {
                        var resourceId =
                            resource.ProtocolResource?.Uri ?? resource.ProtocolResourceTemplate.UriTemplate;
                        if (!VyralMcpCatalog.TryGetByMcpId(resourceId, out var catalog))
                        {
                            if (!options.ConformanceMode) resources!.Remove(resource);
                            continue;
                        }
                        if (!VyralMcpCatalog.IsEnabled(catalog, options))
                            resources!.Remove(resource);
                    }
                    return Task.CompletedTask;
                };
            })
            .WithTools<VyralMcpTools>()
            .WithResources<VyralMcpResources>();

        if (taskStore is not null)
        {
            builder.WithTasks(taskStore, taskOptions =>
            {
                taskOptions.ExecutionModeSelector = context =>
                {
                    var name = context.Params?.Name;
                    if (string.IsNullOrWhiteSpace(name))
                        return McpTaskExecutionMode.Synchronous;
                    if (options.ConformanceMode)
                    {
                        if (name is "slow_compute" or "protocol_error_job" or "confirm_delete" or "multi_input")
                            return McpTaskExecutionMode.Optional;
                        if (name == "failing_job")
                            return McpTaskExecutionMode.Required;
                        if (name == "test_tool_with_task" && context.Params?.InputResponses is { Count: > 0 })
                            return McpTaskExecutionMode.Required;
                    }
                    if (!VyralMcpCatalog.TryGetByMcpId(name, out var catalog))
                        return McpTaskExecutionMode.Synchronous;
                    return catalog.Exposure == "task"
                        ? McpTaskExecutionMode.Required
                        : McpTaskExecutionMode.Synchronous;
                };
            });
        }

        return builder;
    }
}
