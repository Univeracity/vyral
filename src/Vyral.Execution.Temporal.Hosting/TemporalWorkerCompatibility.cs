using Temporalio.Api.Enums.V1;
using Temporalio.Api.TaskQueue.V1;
using Temporalio.Common;
using Temporalio.Worker;

namespace Vyral.Execution.Temporal.Hosting;

internal sealed record TemporalWorkerDeploymentDescriptor(string DeploymentName, string BuildId);

internal sealed record TemporalWorkerPollerQueueStatus(
    int FreshPollers,
    int CurrentBuildPollers = 0,
    int OtherBuildPollers = 0,
    int UnattributedPollers = 0,
    int VersionedPollers = 0,
    int DistinctBuilds = 0,
    bool CompatibilityProbed = false);

internal static class TemporalWorkerCompatibility
{
    internal const string VersioningMode = "unversioned";
    internal const string CompatibilityPolicy = "replay_compatible";

    internal static TemporalWorkerDeploymentDescriptor Resolve(TemporalExecutionOptions? options = null)
    {
        options?.Validate();
        var version = typeof(TemporalExecutionWorker).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        return new TemporalWorkerDeploymentDescriptor(
            options?.WorkerDeploymentName ?? "vyral-execution",
            options?.WorkerBuildId ?? $"vyral-run-coordinator-v1-{version}");
    }

    internal static WorkerDeploymentOptions CreateWorkerDeploymentOptions(
        TemporalWorkerDeploymentDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new WorkerDeploymentOptions(
            new WorkerDeploymentVersion(descriptor.DeploymentName, descriptor.BuildId),
            useWorkerVersioning: false);
    }

    internal static TemporalWorkerPollerQueueStatus Summarize(
        IEnumerable<PollerInfo> pollers,
        TemporalWorkerDeploymentDescriptor expected,
        DateTime nowUtc,
        TimeSpan freshness)
    {
        ArgumentNullException.ThrowIfNull(pollers);
        ArgumentNullException.ThrowIfNull(expected);
        var cutoffUtc = nowUtc.ToUniversalTime() - freshness;
        var fresh = pollers
            .Where(poller => poller.LastAccessTime is not null &&
                poller.LastAccessTime.ToDateTime() >= cutoffUtc)
            .ToList();
        var current = 0;
        var other = 0;
        var unattributed = 0;
        var versioned = 0;
        var builds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var poller in fresh)
        {
            var deployment = poller.DeploymentOptions;
            if (deployment is null ||
                string.IsNullOrWhiteSpace(deployment.DeploymentName) ||
                string.IsNullOrWhiteSpace(deployment.BuildId))
            {
                unattributed++;
                continue;
            }

            builds.Add(TemporalExecutionOptions.HashForDisplay(
                $"{deployment.DeploymentName}\n{deployment.BuildId}"));
            if (deployment.WorkerVersioningMode == WorkerVersioningMode.Versioned)
            {
                versioned++;
                continue;
            }
            if (deployment.WorkerVersioningMode != WorkerVersioningMode.Unversioned)
            {
                unattributed++;
                continue;
            }

            if (string.Equals(deployment.DeploymentName, expected.DeploymentName, StringComparison.Ordinal) &&
                string.Equals(deployment.BuildId, expected.BuildId, StringComparison.Ordinal))
            {
                current++;
            }
            else
            {
                other++;
            }
        }

        return new TemporalWorkerPollerQueueStatus(
            fresh.Count,
            current,
            other,
            unattributed,
            versioned,
            builds.Count,
            CompatibilityProbed: true);
    }
}
