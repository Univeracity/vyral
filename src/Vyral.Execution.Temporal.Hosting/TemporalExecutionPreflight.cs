using System.Globalization;
using System.Text;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.TaskQueue.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;

namespace Vyral.Execution.Temporal.Hosting;

public static class TemporalExecutionPreflightOutcomes
{
    public const string Passed = "passed";
    public const string Warning = "warning";
    public const string Blocker = "blocker";
}

public sealed record TemporalExecutionPreflightCheck
{
    public required string Name { get; init; }
    public required string Outcome { get; init; }
    public required string Code { get; init; }
}

public sealed record TemporalExecutionPreflightResult
{
    public required DateTime CheckedAtUtc { get; init; }
    public required bool Ready { get; init; }
    public required string AdapterVersion { get; init; }
    public required string CoreContractVersion { get; init; }
    public required string Qualification { get; init; }
    public required IReadOnlyList<TemporalExecutionPreflightCheck> Checks { get; init; }
    public required IReadOnlyDictionary<string, string> Details { get; init; }

    public int BlockerCount => Checks.Count(item => item.Outcome == TemporalExecutionPreflightOutcomes.Blocker);
    public int WarningCount => Checks.Count(item => item.Outcome == TemporalExecutionPreflightOutcomes.Warning);
}

internal sealed record TemporalWorkerPollerStatus(
    TemporalWorkerPollerQueueStatus Workflow,
    TemporalWorkerPollerQueueStatus Activity)
{
    public TemporalWorkerPollerStatus(int workflowPollers, int activityPollers)
        : this(CreateCurrentBuildStatus(workflowPollers), CreateCurrentBuildStatus(activityPollers))
    {
    }

    public int WorkflowPollers => Workflow.FreshPollers;
    public int ActivityPollers => Activity.FreshPollers;

    private static TemporalWorkerPollerQueueStatus CreateCurrentBuildStatus(int pollers) => new(
        pollers,
        CurrentBuildPollers: pollers,
        DistinctBuilds: pollers > 0 ? 1 : 0,
        CompatibilityProbed: true);
}

/// <summary>
/// Probes Temporal, task-queue pollers, the Vyral projection, and an isolated object key without
/// starting a workflow or exposing provider topology and credential material in its result.
/// </summary>
public sealed class TemporalExecutionPreflight
{
    private const string Qualification = "prototype_unqualified";
    private const int CoordinatorProbeLimit = 25;
    private const int CoordinatorProbeConcurrency = 8;
    private static readonly byte[] ProbeBody = Encoding.UTF8.GetBytes("vyral-temporal-preflight-v1");
    private static readonly TimeSpan WorkerPollerFreshness = TimeSpan.FromMinutes(2);
    private readonly Func<CancellationToken, Task<bool>> _checkTemporalHealth;
    private readonly Func<CancellationToken, Task<TemporalWorkerPollerStatus?>> _checkWorkerPollers;
    private readonly Func<
        IReadOnlyList<TemporalActiveCoordinator>,
        CancellationToken,
        Task<IReadOnlyList<TemporalActiveCoordinator>>>? _findInactiveCoordinators;
    private readonly ITemporalExecutionRuntimeStore _store;
    private readonly IObjectStore _objects;
    private readonly TemporalExecutionOptions _options;

    public TemporalExecutionPreflight(
        ITemporalClient client,
        ITemporalExecutionRuntimeStore store,
        IObjectStore objects,
        TemporalExecutionOptions options)
        : this(
            CreateHealthProbe(client, options),
            store,
            objects,
            options,
            CreateWorkerPollerProbe(client, options),
            CreateCoordinatorConsistencyProbe(client, options))
    {
    }

    internal TemporalExecutionPreflight(
        Func<CancellationToken, Task<bool>> checkTemporalHealth,
        ITemporalExecutionRuntimeStore store,
        IObjectStore objects,
        TemporalExecutionOptions options,
        Func<CancellationToken, Task<TemporalWorkerPollerStatus?>>? checkWorkerPollers = null,
        Func<
            IReadOnlyList<TemporalActiveCoordinator>,
            CancellationToken,
            Task<IReadOnlyList<TemporalActiveCoordinator>>>? findInactiveCoordinators = null)
    {
        _checkTemporalHealth = checkTemporalHealth ?? throw new ArgumentNullException(nameof(checkTemporalHealth));
        _checkWorkerPollers = checkWorkerPollers ??
            (_ => Task.FromResult<TemporalWorkerPollerStatus?>(null));
        _findInactiveCoordinators = findInactiveCoordinators;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<TemporalExecutionPreflightResult> RunAsync(CancellationToken ct = default)
    {
        var checks = new List<TemporalExecutionPreflightCheck>
        {
            Passed("configuration", "configuration.valid")
        };
        var details = new Dictionary<string, string>(_options.ToDiagnosticMetadata(), StringComparer.Ordinal)
        {
            ["adapterVersion"] = AdapterVersion(),
            ["coreContractVersion"] = CoreContractVersion(),
            ["qualification"] = Qualification,
            ["coordinatorWorkflowType"] = TemporalExecutionProtocolNames.CoordinatorWorkflow,
            ["workerVersioningMode"] = TemporalWorkerCompatibility.VersioningMode,
            ["workerCompatibilityPolicy"] = TemporalWorkerCompatibility.CompatibilityPolicy
        };
        var expectedWorker = TemporalWorkerCompatibility.Resolve(_options);
        details["workerDeploymentNameHash"] = TemporalExecutionOptions.HashForDisplay(
            expectedWorker.DeploymentName);
        details["workerBuildIdHash"] = TemporalExecutionOptions.HashForDisplay(expectedWorker.BuildId);

        await CheckTemporalAsync(checks, ct);
        await CheckWorkerAsync(checks, details, ct);
        var projection = await CheckProjectionAsync(checks, details, ct);
        if (projection is not null)
        {
            await CheckCoordinatorConsistencyAsync(checks, details, ct);
        }
        await CheckObjectStoreAsync(checks, ct);

        return new TemporalExecutionPreflightResult
        {
            CheckedAtUtc = DateTime.UtcNow,
            Ready = checks.All(item => item.Outcome != TemporalExecutionPreflightOutcomes.Blocker),
            AdapterVersion = details["adapterVersion"],
            CoreContractVersion = details["coreContractVersion"],
            Qualification = Qualification,
            Checks = checks,
            Details = details
        };
    }

    private async Task CheckWorkerAsync(
        ICollection<TemporalExecutionPreflightCheck> checks,
        IDictionary<string, string> details,
        CancellationToken ct)
    {
        try
        {
            var status = await _checkWorkerPollers(ct);
            if (status is null)
            {
                checks.Add(Warning("worker", "worker.reachability_not_probed"));
                return;
            }

            details["workflowPollers"] = status.WorkflowPollers.ToString(CultureInfo.InvariantCulture);
            details["activityPollers"] = status.ActivityPollers.ToString(CultureInfo.InvariantCulture);
            details["workerPollerFreshnessSeconds"] = WorkerPollerFreshness.TotalSeconds
                .ToString(CultureInfo.InvariantCulture);
            checks.Add(status.WorkflowPollers > 0 && status.ActivityPollers > 0
                ? Passed("worker", "worker.pollers_active")
                : Warning("worker", "worker.pollers_missing"));
            AddWorkerCompatibilityChecks(status, checks, details);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            checks.Add(Warning("worker", "worker.reachability_check_failed"));
        }
    }

    private static void AddWorkerCompatibilityChecks(
        TemporalWorkerPollerStatus status,
        ICollection<TemporalExecutionPreflightCheck> checks,
        IDictionary<string, string> details)
    {
        if (!status.Workflow.CompatibilityProbed || !status.Activity.CompatibilityProbed)
        {
            checks.Add(Warning("workerCompatibility", "worker.compatibility_not_probed"));
            return;
        }

        AddWorkerQueueDetails("workflow", status.Workflow, details);
        AddWorkerQueueDetails("activity", status.Activity, details);
        checks.Add(status.Workflow.CurrentBuildPollers > 0 && status.Activity.CurrentBuildPollers > 0
            ? Passed("workerCompatibility", "worker.current_build_pollers_active")
            : Warning("workerCompatibility", "worker.current_build_pollers_missing"));
        checks.Add(status.Workflow.VersionedPollers == 0 && status.Activity.VersionedPollers == 0
            ? Passed("workerCompatibility", "worker.versioning_mode_consistent")
            : Warning("workerCompatibility", "worker.unexpected_versioned_pollers"));
        checks.Add(status.Workflow.UnattributedPollers == 0 && status.Activity.UnattributedPollers == 0
            ? Passed("workerCompatibility", "worker.compatibility_metadata_attributed")
            : Warning("workerCompatibility", "worker.compatibility_metadata_missing"));
        checks.Add(status.Workflow.OtherBuildPollers == 0 && status.Activity.OtherBuildPollers == 0
            ? Passed("workerCompatibility", "worker.current_build_only")
            : Warning("workerCompatibility", "worker.mixed_builds_observed"));
    }

    private static void AddWorkerQueueDetails(
        string queueType,
        TemporalWorkerPollerQueueStatus status,
        IDictionary<string, string> details)
    {
        details[$"{queueType}CurrentBuildPollers"] = status.CurrentBuildPollers
            .ToString(CultureInfo.InvariantCulture);
        details[$"{queueType}OtherBuildPollers"] = status.OtherBuildPollers
            .ToString(CultureInfo.InvariantCulture);
        details[$"{queueType}UnattributedPollers"] = status.UnattributedPollers
            .ToString(CultureInfo.InvariantCulture);
        details[$"{queueType}VersionedPollers"] = status.VersionedPollers
            .ToString(CultureInfo.InvariantCulture);
        details[$"{queueType}DistinctBuilds"] = status.DistinctBuilds
            .ToString(CultureInfo.InvariantCulture);
    }

    private async Task CheckTemporalAsync(
        ICollection<TemporalExecutionPreflightCheck> checks,
        CancellationToken ct)
    {
        try
        {
            checks.Add(await _checkTemporalHealth(ct)
                ? Passed("temporal", "temporal.namespace_reachable")
                : Blocker("temporal", "temporal.workflow_service_unhealthy"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RpcException ex) when (
            ex.Code is RpcException.StatusCode.PermissionDenied or RpcException.StatusCode.Unauthenticated)
        {
            checks.Add(Blocker("temporal", "temporal.authorization_failed"));
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            checks.Add(Blocker("temporal", "temporal.namespace_not_found"));
        }
        catch
        {
            checks.Add(Blocker("temporal", "temporal.health_check_failed"));
        }
    }

    private async Task<TemporalExecutionProjectionStatus?> CheckProjectionAsync(
        ICollection<TemporalExecutionPreflightCheck> checks,
        IDictionary<string, string> details,
        CancellationToken ct)
    {
        try
        {
            var status = await _store.GetRuntimeStatusAsync(ct);
            details["projectionSchemaVersion"] = status.SchemaVersion.ToString(CultureInfo.InvariantCulture);
            details["activeRuns"] = status.ActiveRuns.ToString(CultureInfo.InvariantCulture);
            details["activeCoordinators"] = status.ActiveCoordinators.ToString(CultureInfo.InvariantCulture);
            details["pendingStartDispatches"] = status.PendingStartDispatches.ToString(CultureInfo.InvariantCulture);
            details["pendingSignalDispatches"] = status.PendingSignalDispatches.ToString(CultureInfo.InvariantCulture);
            details["pendingCancellationDispatches"] = status.PendingCancellationDispatches.ToString(CultureInfo.InvariantCulture);
            if (status.OldestPendingDispatchAtUtc.HasValue)
            {
                details["oldestPendingDispatchAgeSeconds"] = Math.Max(
                    0,
                    (long)(DateTime.UtcNow - status.OldestPendingDispatchAtUtc.Value).TotalSeconds)
                    .ToString(CultureInfo.InvariantCulture);
            }
            checks.Add(status.SchemaVersion > 0
                ? Passed("projection", "projection.schema_supported")
                : Blocker("projection", "projection.schema_invalid"));
            var pending = status.PendingStartDispatches +
                status.PendingSignalDispatches +
                status.PendingCancellationDispatches;
            checks.Add(pending == 0
                ? Passed("outbox", "outbox.drained")
                : Warning("outbox", "outbox.delivery_pending"));
            return status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            checks.Add(Blocker("projection", "projection.health_check_failed"));
            return null;
        }
    }

    private async Task CheckCoordinatorConsistencyAsync(
        ICollection<TemporalExecutionPreflightCheck> checks,
        IDictionary<string, string> details,
        CancellationToken ct)
    {
        try
        {
            var snapshot = await _store.GetActiveCoordinatorSnapshotAsync(CoordinatorProbeLimit, ct);
            details["activeCoordinators"] = snapshot.TotalCount.ToString(CultureInfo.InvariantCulture);
            details["coordinatorProbeLimit"] = CoordinatorProbeLimit.ToString(CultureInfo.InvariantCulture);
            details["coordinatorsExamined"] = snapshot.Coordinators.Count.ToString(CultureInfo.InvariantCulture);
            if (snapshot.TotalCount == 0)
            {
                details["staleRunsDetected"] = "0";
                details["staleRuns"] = "0";
                checks.Add(Passed("projection", "projection.coordinators_consistent"));
                checks.Add(Passed("projection", "projection.coordinator_check_complete"));
                return;
            }
            if (_findInactiveCoordinators is null)
            {
                checks.Add(Warning("projection", "projection.coordinator_check_unavailable"));
                return;
            }

            var inactive = await _findInactiveCoordinators(snapshot.Coordinators, ct);
            var stale = 0;
            foreach (var coordinator in inactive)
            {
                if (await _store.IsActiveCoordinatorAsync(
                    coordinator.WorkflowId,
                    coordinator.Generation,
                    ct))
                {
                    stale++;
                }
            }
            details["staleRunsDetected"] = stale.ToString(CultureInfo.InvariantCulture);
            if (snapshot.Coordinators.Count == snapshot.TotalCount)
            {
                details["staleRuns"] = stale.ToString(CultureInfo.InvariantCulture);
            }
            checks.Add(stale == 0
                ? Passed("projection", "projection.coordinators_consistent")
                : Warning("projection", "projection.stale_coordinators_detected"));
            checks.Add(snapshot.Coordinators.Count == snapshot.TotalCount
                ? Passed("projection", "projection.coordinator_check_complete")
                : Warning("projection", "projection.coordinator_check_partial"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            checks.Add(Warning("projection", "projection.coordinator_check_failed"));
        }
    }

    private async Task CheckObjectStoreAsync(
        ICollection<TemporalExecutionPreflightCheck> checks,
        CancellationToken ct)
    {
        var key = $"_preflight/{Guid.NewGuid():N}.txt";
        ObjectInfo? written = null;
        var writeAttempted = false;
        var healthy = false;
        var cleanupFailed = false;
        try
        {
            await using var body = new MemoryStream(ProbeBody, writable: false);
            writeAttempted = true;
            written = await _objects.PutObjectAsync(new ObjectWriteRequest
            {
                Container = _options.ArtifactObjectContainer,
                Key = key,
                Content = body,
                ContentType = "text/plain",
                IfNoneMatch = "*",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["purpose"] = "preflight"
                }
            }, ct);
            var read = await _objects.GetObjectAsync(new ObjectReadRequest
            {
                Container = _options.ArtifactObjectContainer,
                Key = key
            }, ct);
            if (read is not null)
            {
                await using var content = read.Content;
                using var buffer = new MemoryStream();
                await content.CopyToAsync(buffer, ct);
                healthy = buffer.ToArray().AsSpan().SequenceEqual(ProbeBody);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            healthy = false;
        }
        finally
        {
            if (writeAttempted)
            {
                try
                {
                    await _objects.DeleteObjectAsync(new ObjectDeleteRequest
                    {
                        Container = written?.Container ?? _options.ArtifactObjectContainer,
                        Key = written?.Key ?? key,
                        IfMatch = written?.Etag
                    }, CancellationToken.None);
                    cleanupFailed = await _objects.GetObjectAsync(new ObjectReadRequest
                    {
                        Container = written?.Container ?? _options.ArtifactObjectContainer,
                        Key = written?.Key ?? key
                    }, CancellationToken.None) is not null;
                }
                catch
                {
                    cleanupFailed = true;
                }
            }
        }

        checks.Add(healthy
            ? Passed("objectStore", "object_store.read_write_passed")
            : Blocker("objectStore", "object_store.read_write_failed"));
        checks.Add(cleanupFailed
            ? Blocker("objectStore", "object_store.cleanup_failed")
            : Passed("objectStore", "object_store.cleanup_passed"));
    }

    private static TemporalExecutionPreflightCheck Passed(string name, string code) => new()
    {
        Name = name,
        Outcome = TemporalExecutionPreflightOutcomes.Passed,
        Code = code
    };

    private static TemporalExecutionPreflightCheck Warning(string name, string code) => new()
    {
        Name = name,
        Outcome = TemporalExecutionPreflightOutcomes.Warning,
        Code = code
    };

    private static TemporalExecutionPreflightCheck Blocker(string name, string code) => new()
    {
        Name = name,
        Outcome = TemporalExecutionPreflightOutcomes.Blocker,
        Code = code
    };

    private static string AdapterVersion() =>
        typeof(TemporalExecutionRuntimeAdapter).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private static string CoreContractVersion() =>
        typeof(IExecutionRuntime).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private static Func<CancellationToken, Task<bool>> CreateHealthProbe(
        ITemporalClient client,
        TemporalExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        return async ct =>
        {
            var rpc = new RpcOptions
            {
                CancellationToken = ct,
                Timeout = TimeSpan.FromSeconds(5),
                Retry = false
            };
            if (!await client.Connection.CheckHealthAsync(client.Connection.WorkflowService, rpc))
                return false;
            _ = await client.Connection.WorkflowService.DescribeNamespaceAsync(
                new DescribeNamespaceRequest { Namespace = options.Namespace },
                rpc);
            return true;
        };
    }

    private static Func<CancellationToken, Task<TemporalWorkerPollerStatus?>> CreateWorkerPollerProbe(
        ITemporalClient client,
        TemporalExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        return async ct =>
        {
            var rpc = new RpcOptions
            {
                CancellationToken = ct,
                Timeout = TimeSpan.FromSeconds(5),
                Retry = false
            };
            async Task<TemporalWorkerPollerQueueStatus> PollerStatusAsync(TaskQueueType type)
            {
                var response = await client.WorkflowService.DescribeTaskQueueAsync(
                    new DescribeTaskQueueRequest
                    {
                        Namespace = options.Namespace,
                        TaskQueue = new TaskQueue
                        {
                            Name = options.TaskQueue,
                            Kind = TaskQueueKind.Normal
                        },
                        TaskQueueType = type
                    },
                    rpc);
                return TemporalWorkerCompatibility.Summarize(
                    response.Pollers,
                    TemporalWorkerCompatibility.Resolve(options),
                    DateTime.UtcNow,
                    WorkerPollerFreshness);
            }

            return new TemporalWorkerPollerStatus(
                await PollerStatusAsync(TaskQueueType.Workflow),
                await PollerStatusAsync(TaskQueueType.Activity));
        };
    }

    private static Func<
        IReadOnlyList<TemporalActiveCoordinator>,
        CancellationToken,
        Task<IReadOnlyList<TemporalActiveCoordinator>>>
        CreateCoordinatorConsistencyProbe(
            ITemporalClient client,
            TemporalExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        return async (coordinators, ct) =>
        {
            using var concurrency = new SemaphoreSlim(CoordinatorProbeConcurrency);
            var tasks = coordinators.Select(async coordinator =>
            {
                await concurrency.WaitAsync(ct);
                try
                {
                    try
                    {
                        var response = await client.WorkflowService.DescribeWorkflowExecutionAsync(
                            new DescribeWorkflowExecutionRequest
                            {
                                Namespace = options.Namespace,
                                Execution = new Temporalio.Api.Common.V1.WorkflowExecution
                                {
                                    WorkflowId = coordinator.WorkflowId
                                }
                            },
                            new RpcOptions
                            {
                                CancellationToken = ct,
                                Timeout = TimeSpan.FromSeconds(5),
                                Retry = false
                            });
                        return response.WorkflowExecutionInfo.Status == WorkflowExecutionStatus.Running
                            ? null
                            : coordinator;
                    }
                    catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
                    {
                        return coordinator;
                    }
                }
                finally
                {
                    concurrency.Release();
                }
            });
            return (await Task.WhenAll(tasks))
                .Where(coordinator => coordinator is not null)
                .Select(coordinator => coordinator!)
                .ToList();
        };
    }

    internal static int CountFreshPollers(
        IEnumerable<PollerInfo> pollers,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(pollers);
        var cutoffUtc = nowUtc.ToUniversalTime() - WorkerPollerFreshness;
        return pollers.Count(poller =>
            poller.LastAccessTime is not null &&
            poller.LastAccessTime.ToDateTime() >= cutoffUtc);
    }
}
