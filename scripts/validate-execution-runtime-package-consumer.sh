#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PACKAGE_VERSION="${VYRAL_EXECUTION_PACKAGE_VERSION:-0.2.0}"
WORK_ROOT="${TMPDIR:-/tmp}/vyral-execution-package-consumer-$(date +%s)-$$"
PACKAGES_DIR="$WORK_ROOT/packages"
CONSUMER_DIR="$WORK_ROOT/consumer"
CONSUMER_NUGET_PACKAGES="$WORK_ROOT/consumer-nuget-cache"

cleanup() {
  if [[ "${VYRAL_KEEP_EXECUTION_PACKAGE_CONSUMER:-0}" != "1" ]]; then
    rm -rf "$WORK_ROOT"
  else
    echo "kept-work-root=$WORK_ROOT"
  fi
}
trap cleanup EXIT

mkdir -p "$PACKAGES_DIR"

scripts/verify-dotnet-lockfiles.sh
dotnet pack src/Vyral.Primitives/Vyral.Primitives.csproj --no-restore -o "$PACKAGES_DIR"
dotnet pack src/Vyral.Abstractions/Vyral.Abstractions.csproj --no-restore -o "$PACKAGES_DIR"
dotnet pack src/Vyral.Execution/Vyral.Execution.csproj --no-restore -o "$PACKAGES_DIR"
dotnet pack src/Vyral.Execution.Local/Vyral.Execution.Local.csproj --no-restore -o "$PACKAGES_DIR"
dotnet pack src/Vyral.Execution.Aws/Vyral.Execution.Aws.csproj --no-restore -o "$PACKAGES_DIR"
dotnet pack src/Vyral.Execution.AzureDurable/Vyral.Execution.AzureDurable.csproj --no-restore -o "$PACKAGES_DIR"
dotnet pack src/Vyral.Execution.AzureDurable.Functions/Vyral.Execution.AzureDurable.Functions.csproj --no-restore -o "$PACKAGES_DIR"
dotnet pack src/Vyral.Execution.Temporal/Vyral.Execution.Temporal.csproj --no-restore -o "$PACKAGES_DIR"
dotnet pack src/Vyral.Execution.Temporal.Hosting/Vyral.Execution.Temporal.Hosting.csproj --no-restore -o "$PACKAGES_DIR"
dotnet pack src/Vyral.Execution.Temporal.Postgres/Vyral.Execution.Temporal.Postgres.csproj --no-restore -o "$PACKAGES_DIR"
dotnet pack src/Vyral.Execution.WorkerClient/Vyral.Execution.WorkerClient.csproj --no-restore -o "$PACKAGES_DIR"

mkdir -p "$CONSUMER_DIR"
cp samples/Vyral.Execution.PackageConsumerTemplate/Vyral.Execution.PackageConsumerTemplate.csproj \
  "$CONSUMER_DIR/VyralExecutionPackageConsumer.csproj"
cp samples/Vyral.Execution.PackageConsumerTemplate/Program.cs "$CONSUMER_DIR/Program.cs"

cat > "$CONSUMER_DIR/NuGet.config" <<NUGET
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-vyral" value="$PACKAGES_DIR" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
NUGET

dotnet add "$CONSUMER_DIR/VyralExecutionPackageConsumer.csproj" package Vyral.Execution.AzureDurable --version "$PACKAGE_VERSION"
dotnet add "$CONSUMER_DIR/VyralExecutionPackageConsumer.csproj" package Vyral.Execution.AzureDurable.Functions --version "$PACKAGE_VERSION"
dotnet add "$CONSUMER_DIR/VyralExecutionPackageConsumer.csproj" package Vyral.Execution.Aws --version "$PACKAGE_VERSION"
dotnet add "$CONSUMER_DIR/VyralExecutionPackageConsumer.csproj" package Vyral.Execution.Temporal --version "$PACKAGE_VERSION"
dotnet add "$CONSUMER_DIR/VyralExecutionPackageConsumer.csproj" package Vyral.Execution.Temporal.Hosting --version "$PACKAGE_VERSION"
dotnet add "$CONSUMER_DIR/VyralExecutionPackageConsumer.csproj" package Vyral.Execution.Temporal.Postgres --version "$PACKAGE_VERSION"
dotnet add "$CONSUMER_DIR/VyralExecutionPackageConsumer.csproj" package Vyral.Execution.WorkerClient --version "$PACKAGE_VERSION"

cat > "$CONSUMER_DIR/AzurePackageProbe.cs" <<'CSHARP'
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using Vyral.Execution.Aws;
using Vyral.Execution.AzureDurable;
using Vyral.Execution.AzureDurable.Functions;
using Vyral.Execution.Temporal;
using Vyral.Execution.Temporal.Hosting;
using Vyral.Execution.Temporal.Postgres;
using Vyral.Execution.WorkerClient;

internal static class AzurePackageProbe
{
    [ModuleInitializer]
    public static void Verify()
    {
        var descriptor = AzureDurableExecutionDialect.BuildAdapterDescriptor();
        if (descriptor.RuntimeKind != AzureDurableExecutionRuntimeKindIds.DurableFunctions)
        {
            throw new InvalidOperationException("Azure Durable package did not expose the expected runtime kind.");
        }

        var localSettings = new AzureDurableLocalHostSmokeOptions().BuildLocalSettings();
        if (localSettings[AzureDurableLocalHostSmokeOptions.AzureWebJobsStorageSettingName] !=
            AzureDurableLocalHostSmokeOptions.LocalDevelopmentStorageConnectionString)
        {
            throw new InvalidOperationException("Azure local host smoke settings are not local-only.");
        }

        _ = typeof(AzureDurableFunctionsBridge);
        _ = typeof(AwsDynamoExecutionRuntimeAdapter);
        _ = typeof(TemporalExecutionWorker);
        _ = typeof(TemporalExecutionRuntimeAdapter);
        _ = typeof(PostgresTemporalExecutionProjectionStore);
        _ = typeof(IExecutionWorkerTransport);
        _ = typeof(ExecutionPluginWorker);
        _ = typeof(InProcessExecutionWorkerTransport);
        var temporalOptions = new TemporalExecutionOptions
        {
            TargetHost = "127.0.0.1:7233",
            RequireTls = false
        };
        temporalOptions.Validate();
        var temporalServices = new ServiceCollection();
        temporalServices
            .AddVyralTemporalExecution(temporalOptions)
            .AddHostedWorker(new TemporalExecutionWorkerHostOptions
            {
                WorkerId = "package-probe-worker"
            });
        if (!temporalServices.Any(item => item.ServiceType == typeof(TemporalExecutionRuntimeAdapter)))
        {
            throw new InvalidOperationException("Temporal hosting package did not expose its DI composition.");
        }
        if (TemporalExecutionRuntimeKindIds.Temporal != "temporal.workflow")
        {
            throw new InvalidOperationException("Temporal package did not expose the expected runtime kind.");
        }
        _ = new AwsSqsExecutionDispatchOptions
        {
            QueueUrl = "https://sqs.us-east-1.amazonaws.com/ACCOUNT/vyral-execution"
        };
        using var httpClient = new HttpClient();
        _ = new ExecutionWorkerClient(httpClient, new ExecutionWorkerClientOptions
        {
            BaseUri = new Uri("https://vyral.example/"),
            WorkerId = "package-probe-worker",
            HandlerIds = ["package.probe.handler"]
        });
    }
}
CSHARP

export NUGET_PACKAGES="$CONSUMER_NUGET_PACKAGES"
dotnet restore "$CONSUMER_DIR/VyralExecutionPackageConsumer.csproj"
dotnet run --project "$CONSUMER_DIR/VyralExecutionPackageConsumer.csproj" --no-restore
