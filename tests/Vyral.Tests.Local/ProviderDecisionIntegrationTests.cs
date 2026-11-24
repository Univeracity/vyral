using Vyral.Execution.Local;
using Vyral.Local;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Local;
using Vyral.Server;

namespace Vyral.Tests.Local;

public sealed class ProviderDecisionIntegrationTests
{
    [Fact]
    public async Task DurableProviderJobEvaluatesDecisionAndBindsPolicyToReplayIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "vyral-provider-decision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runtime = new LocalExecutionRuntime(new()
            {
                DatabasePath = Path.Combine(root, "execution.sqlite"),
                ArtifactDirectory = Path.Combine(root, "artifacts")
            });
            var target = new ObservingTarget(new DeterministicAiProviderTarget());
            var traces = new SqliteTraceStore(Path.Combine(root, "traces.sqlite"));
            await traces.InitializeAsync();
            var jobs = new ExecutionRuntimeProviderRunJobAdapter(
                runtime,
                new ProviderTargetRegistry(new[] { target }),
                traces,
                new ProviderRunGuard(new()));
            var request = Request("1.0.0");

            var admitted = await jobs.StartAsync(target.Profile.Id, request, idempotencyKey: "reuse-fixture");
            await runtime.DispatchReadyRunsAsync();
            ProviderRunJob? completed = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                completed = await jobs.GetAsync(admitted.Id);
                if (completed is not null && completed.Status is not (ProviderJobStatus.Queued or ProviderJobStatus.Running)) break;
                await Task.Delay(20);
            }

            Assert.NotNull(completed);
            Assert.Equal(ProviderJobStatus.Succeeded, completed!.Status);
            var decision = Assert.IsType<AiDecisionReceipt>(completed.Result!.Decision);
            Assert.Equal(AiDecisionOutcomes.Accept, decision.Outcome);
            Assert.Equal("mechanics-routing", decision.PolicyId);
            Assert.Equal("1.0.0", decision.PolicyVersion);
            Assert.Equal(AiValidationStatuses.Deterministic, decision.ValidationStatus);
            Assert.False(decision.Calibrated);
            Assert.Equal(completed.Admission.RequestHash, completed.RequestHash);
            Assert.False(target.ReceivedDecisionPolicy);

            var changedPolicy = Request("2.0.0");
            changedPolicy.CorrelationId = request.CorrelationId;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                jobs.StartAsync(target.Profile.Id, changedPolicy, idempotencyKey: "reuse-fixture"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InvalidPolicyProducesAStableRejectedJobWithoutInvokingTheProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "vyral-provider-decision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runtime = new LocalExecutionRuntime(new()
            {
                DatabasePath = Path.Combine(root, "execution.sqlite"),
                ArtifactDirectory = Path.Combine(root, "artifacts")
            });
            var target = new ObservingTarget(new DeterministicAiProviderTarget());
            var traces = new SqliteTraceStore(Path.Combine(root, "traces.sqlite"));
            await traces.InitializeAsync();
            var jobs = new ExecutionRuntimeProviderRunJobAdapter(
                runtime,
                new ProviderTargetRegistry(new[] { target }),
                traces,
                new ProviderRunGuard(new()));
            var request = Request("invalid");
            request.DecisionPolicy!.Accept.Values.Add("unreachable_fixture_choice");

            var admitted = await jobs.StartAsync(target.Profile.Id, request);
            await runtime.DispatchReadyRunsAsync();
            ProviderRunJob? completed = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                completed = await jobs.GetAsync(admitted.Id);
                if (completed is not null && completed.Status is not (ProviderJobStatus.Queued or ProviderJobStatus.Running)) break;
                await Task.Delay(20);
            }

            Assert.NotNull(completed);
            Assert.Equal(ProviderJobStatus.Rejected, completed!.Status);
            Assert.Equal("invalid_decision_policy", completed.ProviderStatus);
            Assert.Null(completed.Result!.Decision);
            Assert.False(target.WasInvoked);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ProviderRunRequest Request(string policyVersion) => new()
    {
        Capability = ProviderCapabilityIds.AiJudge,
        Operation = "run",
        Mode = ProviderModes.Mechanics,
        Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
        {
            Context = "A fictional equipment record.",
            Questions = new()
            {
                new()
                {
                    Id = "reuse",
                    Type = AiJudgeQuestionTypes.Choice,
                    Prompt = "May this record be reused?",
                    Options = new()
                    {
                        new() { Id = "same_item", Label = "same catalog item" },
                        new() { Id = "different_item", Label = "different catalog item" }
                    }
                }
            }
        }),
        DecisionPolicy = new()
        {
            Id = "mechanics-routing",
            Version = policyVersion,
            QuestionId = "reuse",
            AnswerKind = AiDecisionAnswerKinds.Choice,
            Accept = new() { Operator = AiDecisionOperators.In, Values = new() { "same_item", "different_item" } },
            Reject = new() { Operator = AiDecisionOperators.In, Values = new() { "unreachable_fixture_choice" } },
            Evidence = new()
            {
                RequireCalibrated = false,
                AllowedValidationStatuses = new() { AiValidationStatuses.Deterministic }
            },
            EscalationRoute = "manual-review"
        }
    };

    private sealed class ObservingTarget(IProviderTarget inner) : IProviderTarget
    {
        public bool ReceivedDecisionPolicy { get; private set; }
        public bool WasInvoked { get; private set; }
        public ProviderProfile Profile => inner.Profile;
        public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities => inner.Capabilities;

        public Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default)
        {
            WasInvoked = true;
            ReceivedDecisionPolicy |= request.DecisionPolicy is not null;
            return inner.RunAsync(request, ct);
        }
    }
}
