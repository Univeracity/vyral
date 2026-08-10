using System.Text.Json.Nodes;
using Vyral.Execution;

namespace Vyral.Tests.Conformance;

public sealed class ExecutionContractValidationTests
{
    [Fact]
    public void ExecutionRunLifecycle_DefinesPortableStateTransitions()
    {
        Assert.True(ExecutionRunLifecycle.CanCreateAs(ExecutionRunStatuses.Queued));
        Assert.True(ExecutionRunLifecycle.CanCreateAs(ExecutionRunStatuses.Waiting));
        Assert.True(ExecutionRunLifecycle.CanCreateAs(ExecutionRunStatuses.Rejected));
        Assert.True(ExecutionRunLifecycle.CanTransition(ExecutionRunStatuses.Queued, ExecutionRunStatuses.Running));
        Assert.True(ExecutionRunLifecycle.CanTransition(ExecutionRunStatuses.Running, ExecutionRunStatuses.Succeeded));
        Assert.True(ExecutionRunLifecycle.CanTransition(ExecutionRunStatuses.Failed, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.Retry));
        Assert.True(ExecutionRunLifecycle.CanTransition(ExecutionRunStatuses.Running, ExecutionRunStatuses.Queued, ExecutionTransitionKind.Recovery));
        Assert.True(ExecutionRunLifecycle.CanTransition(ExecutionRunStatuses.Running, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.DurableWait));
        Assert.False(ExecutionRunLifecycle.CanTransition(ExecutionRunStatuses.Succeeded, ExecutionRunStatuses.Running));
        Assert.False(ExecutionRunLifecycle.CanTransition(ExecutionRunStatuses.Succeeded, ExecutionRunStatuses.Queued, ExecutionTransitionKind.Recovery));
        Assert.False(ExecutionRunLifecycle.CanTransition(ExecutionRunStatuses.Cancelled, ExecutionRunStatuses.Queued, ExecutionTransitionKind.Recovery));
        Assert.False(ExecutionRunLifecycle.CanTransition(ExecutionRunStatuses.Queued, ExecutionRunStatuses.Succeeded));
    }

    [Fact]
    public void ExecutionCapabilityCatalog_DefinesPortableBaselineAndCoordinationCapabilities()
    {
        Assert.Contains(ExecutionCapabilityIds.DurableRuns, ExecutionCapabilityCatalog.RequiredAdapterCapabilities);
        Assert.Contains(ExecutionCapabilityIds.Idempotency, ExecutionCapabilityCatalog.RequiredAdapterCapabilities);
        Assert.Contains(ExecutionCapabilityIds.LocalDispatch, ExecutionCapabilityCatalog.DispatchCapabilities);
        Assert.Contains(ExecutionCapabilityIds.RemoteOrchestration, ExecutionCapabilityCatalog.DispatchCapabilities);
        Assert.Contains(ExecutionCapabilityIds.InProcessHandlers, ExecutionCapabilityCatalog.ExecutionModelCapabilities);
        Assert.Contains(ExecutionCapabilityIds.ExternalWorkers, ExecutionCapabilityCatalog.ExecutionModelCapabilities);
        Assert.Contains(ExecutionCapabilityIds.DurableTimers, ExecutionCapabilityCatalog.CoordinationCapabilities);
        Assert.True(ExecutionCapabilityCatalog.IsPortableCapability(ExecutionCapabilityIds.DurableWaits));
        Assert.True(ExecutionCapabilityCatalog.IsPortableCapability(ExecutionCapabilityIds.TraceHistory));
        Assert.False(ExecutionCapabilityCatalog.IsPortableCapability("provider.private"));
        Assert.True(ExecutionCapabilityCatalog.Supports(
            new[] { "provider.private", ExecutionCapabilityIds.DurableRuns },
            ExecutionCapabilityIds.DurableRuns));
    }

    [Fact]
    public void ExecutionContractValidator_RejectsAdapterDescriptorMissingPortableBaseline()
    {
        var descriptor = new ExecutionRuntimeAdapterDescriptor
        {
            AdapterId = "test-adapter",
            RuntimeKind = "test.runtime",
            DisplayName = "Test adapter",
            Version = "1.0.0",
            Capabilities =
            {
                ExecutionCapabilityIds.LocalDispatch,
                ExecutionCapabilityIds.InProcessHandlers,
                ExecutionCapabilityIds.DurableRuns,
                ExecutionCapabilityIds.Cancellation,
                ExecutionCapabilityIds.Artifacts,
                ExecutionCapabilityIds.TraceHistory,
                ExecutionCapabilityIds.Idempotency
            }
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExecutionContractValidator.ValidateAdapterDescriptor(descriptor));
        Assert.Contains(ExecutionCapabilityIds.Retries, error.Message);
    }

    [Fact]
    public void ExecutionContractValidator_RejectsAdapterDescriptorWithoutDispatchModel()
    {
        var descriptor = CreatePortableDescriptor();
        descriptor.Capabilities.Remove(ExecutionCapabilityIds.LocalDispatch);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExecutionContractValidator.ValidateAdapterDescriptor(descriptor));
        Assert.Contains(ExecutionCapabilityIds.RemoteOrchestration, error.Message);
    }

    [Fact]
    public void ExecutionContractValidator_RejectsAdapterDescriptorWithoutExecutionModel()
    {
        var descriptor = CreatePortableDescriptor();
        descriptor.Capabilities.Remove(ExecutionCapabilityIds.InProcessHandlers);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExecutionContractValidator.ValidateAdapterDescriptor(descriptor));
        Assert.Contains(ExecutionCapabilityIds.ExternalWorkers, error.Message);
    }

    [Fact]
    public void ExecutionContractValidator_AllowsExtensionCapabilitiesWithoutMakingThemPortable()
    {
        var descriptor = CreatePortableDescriptor();
        descriptor.Capabilities.Add("provider.private-extension");

        ExecutionContractValidator.ValidateAdapterDescriptor(descriptor);

        Assert.False(ExecutionCapabilityCatalog.IsPortableCapability("provider.private-extension"));
    }

    [Fact]
    public void ExecutionContractValidator_RejectsDuplicateOrWhitespaceCapabilityIds()
    {
        var duplicate = CreatePortableDescriptor();
        duplicate.Capabilities.Add(ExecutionCapabilityIds.DurableRuns);
        var duplicateError = Assert.Throws<InvalidOperationException>(() =>
            ExecutionContractValidator.ValidateAdapterDescriptor(duplicate));
        Assert.Contains("duplicated", duplicateError.Message);

        var whitespace = CreatePortableDescriptor();
        whitespace.Capabilities.Add(" provider.private-extension ");
        var whitespaceError = Assert.Throws<InvalidOperationException>(() =>
            ExecutionContractValidator.ValidateAdapterDescriptor(whitespace));
        Assert.Contains("whitespace", whitespaceError.Message);
    }

    [Fact]
    public void ExecutionContractValidator_RejectsOversizedPayloads()
    {
        var limits = new ExecutionRuntimeLimits { MaxPayloadBytes = 16 };
        var request = new ExecutionRunRequest
        {
            HandlerId = "test.handler",
            Payload = new JsonObject
            {
                ["value"] = new string('x', 64)
            }
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExecutionContractValidator.ValidateRunRequest(request, limits));
        Assert.Contains("Run payload", error.Message);
    }

    [Fact]
    public void ExecutionContractValidator_RejectsInvalidRunUpdates()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ExecutionContractValidator.ValidateRunUpdate(new ExecutionRunUpdate
            {
                Status = "domain-specific",
                Progress = 1.2
            }));

        Assert.Contains("Run update status", error.Message);
    }

    [Fact]
    public void ExecutionContractValidator_RejectsExternalWorkerProgressLifecycleTransitions()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ExecutionContractValidator.ValidateExternalWorkerReportRequest(new ExecutionExternalWorkerReportRequest
            {
                LeaseKey = "lease-a",
                LeaseToken = "token-a",
                WorkerId = "worker-a",
                Update = new ExecutionRunUpdate { Status = ExecutionRunStatuses.Succeeded }
            }));

        Assert.Contains("completion operation", error.Message);
    }

    [Fact]
    public void ExecutionContractValidator_BoundsTraceMessages()
    {
        var value = ExecutionContractValidator.BoundText(new string('x', 40), 20);

        Assert.NotNull(value);
        Assert.Equal(20, value!.Length);
        Assert.EndsWith("[truncated]", value);
    }

    private static ExecutionRuntimeAdapterDescriptor CreatePortableDescriptor()
    {
        return new ExecutionRuntimeAdapterDescriptor
        {
            AdapterId = "test-adapter",
            RuntimeKind = "test.runtime",
            DisplayName = "Test adapter",
            Version = "1.0.0",
            Capabilities =
            {
                ExecutionCapabilityIds.LocalDispatch,
                ExecutionCapabilityIds.InProcessHandlers,
                ExecutionCapabilityIds.DurableRuns,
                ExecutionCapabilityIds.Cancellation,
                ExecutionCapabilityIds.Retries,
                ExecutionCapabilityIds.Artifacts,
                ExecutionCapabilityIds.TraceHistory,
                ExecutionCapabilityIds.Idempotency
            }
        };
    }
}
