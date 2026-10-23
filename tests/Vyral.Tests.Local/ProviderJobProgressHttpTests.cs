using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vyral.Execution;
using Vyral.Providers.Abstractions;

namespace Vyral.Tests.Local;

public class ProviderJobProgressHttpTests
{
    [Fact]
    public async Task ServerTimerClassifiesCooperativeCancellationAsTimeout()
    {
        var root = Path.Combine(Path.GetTempPath(), "vyral-cooperative-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = new GatedTarget { CooperativeCancellation = true };
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["DatabasePath"] = Path.Combine(root, "state.sqlite"),
                        ["ObjectsPath"] = Path.Combine(root, "objects") }));
                builder.ConfigureTestServices(services => services.AddSingleton(new ProviderTargetRegistry(new[] { target })));
            });
            var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync($"/providers/{target.Profile.Id}/jobs", new ProviderRunRequest
            { Capability = ProviderCapabilityIds.AiChat, Operation = "run", Mode = ProviderModes.Advisory, TimeoutSeconds = 1 });
            var job = (await response.Content.ReadFromJsonAsync<ProviderRunJob>())!;
            await target.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ProviderRunJob? final = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                final = await client.GetFromJsonAsync<ProviderRunJob>($"/provider-jobs/{job.Id}");
                if (final!.Status is not (ProviderJobStatus.Queued or ProviderJobStatus.Running)) break;
                await Task.Delay(30);
            }
            Assert.Equal(ProviderJobStatus.TimedOut, final!.Status);
            Assert.Equal(ProviderFailureClasses.Timeout, final.Result!.FailureClass);
            Assert.Equal(1, target.Calls);
        }
        finally { target.Release.TrySetResult(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RepeatedHttpObservationReadsPersistedMetadataWithoutInvocationOrDeadlineRenewal()
    {
        var root = Path.Combine(Path.GetTempPath(), "vyral-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = new GatedTarget();
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["DatabasePath"] = Path.Combine(root, "state.sqlite"),
                        ["ObjectsPath"] = Path.Combine(root, "objects") }));
                builder.ConfigureTestServices(services => services.AddSingleton(new ProviderTargetRegistry(new[] { target })));
            });
            var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync($"/providers/{target.Profile.Id}/jobs", new ProviderRunRequest
            {
                Capability = ProviderCapabilityIds.AiChat, Operation = "run", Mode = ProviderModes.Advisory,
                TimeoutSeconds = 20, Payload = new JsonObject { ["secret"] = "PRIVATE-INPUT" }
            });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var job = (await response.Content.ReadFromJsonAsync<ProviderRunJob>())!;
            await target.Observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var runtime = factory.Services.GetRequiredService<IExecutionRuntime>();
            var before = (await runtime.GetRunAsync(job.Id, false))!;
            var contract = (await client.GetFromJsonAsync<JsonObject>("/contracts/schemas/vyral-public.schema.json"))!;
            var schema = contract["$defs"]!["ProviderRunJobProgress"]!.AsObject();
            DateTimeOffset? originalDeadline = null;
            for (var poll = 0; poll < 3; poll++)
            {
                var json = await client.GetStringAsync($"/provider-jobs/{job.Id}/progress");
                var projection = JsonNode.Parse(json)!.AsObject();
                Assert.Equal(schema["properties"]!.AsObject().Select(item => item.Key).Order(),
                    projection.Select(item => item.Key).Order());
                Assert.Contains(projection["phase"]!.GetValue<string>(),
                    schema["properties"]!["phase"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>()));
                Assert.False(schema["properties"]!["nativePhaseAvailable"]!.AsObject().ContainsKey("const"));
                Assert.DoesNotContain("PRIVATE-INPUT", json); Assert.DoesNotContain("PRIVATE-OUTPUT", json);
                Assert.Equal("runtime_binding", projection["phase"]!.GetValue<string>());
                Assert.True(projection["nativePhaseAvailable"]!.GetValue<bool>());
                var observation = projection["observation"]!.AsObject();
                originalDeadline ??= observation["deadlineUtc"]!.GetValue<DateTimeOffset>();
                Assert.Equal(originalDeadline, observation["deadlineUtc"]!.GetValue<DateTimeOffset>());
                Assert.Equal(1, observation["sequence"]!.GetValue<long>());
                Assert.Null(observation["promptTokens"]);
            }
            var after = (await runtime.GetRunAsync(job.Id, false))!;
            Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
            Assert.Equal(before.PayloadHash, after.PayloadHash); Assert.Equal(before.Attempt, after.Attempt);
            Assert.Equal(1, target.Calls);
            target.Release.TrySetResult();
            JsonObject? terminal = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                terminal = (await client.GetFromJsonAsync<JsonObject>($"/provider-jobs/{job.Id}/progress"))!;
                if (terminal["phase"]!.GetValue<string>() == "terminal") break;
                await Task.Delay(20);
            }
            Assert.Equal("terminal", terminal!["phase"]!.GetValue<string>());
            Assert.Equal("Succeeded", terminal["status"]!.GetValue<string>());
            Assert.False(terminal["nativePhaseAvailable"]!.GetValue<bool>());
            Assert.False(terminal["observation"]!["isCurrent"]!.GetValue<bool>());
            Assert.Equal(originalDeadline, terminal["observation"]!["deadlineUtc"]!.GetValue<DateTimeOffset>());
            Assert.Equal(1, target.Calls);
        }
        finally { target.Release.TrySetResult(); Directory.Delete(root, true); }
    }

    private sealed class GatedTarget : IProviderProgressTarget
    {
        public bool CooperativeCancellation { get; init; }
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public ProviderProfile Profile { get; } = new() { Id = "fixture-progress", ConfigHash = "sha256:" + new string('a', 64) };
        public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities { get; } = new[]
        { new ProviderCapabilityDescriptor { Id = ProviderCapabilityIds.AiChat, Operations = new() { "run" } } };
        public Task<ProviderRunResult> RunAsync(ProviderRunRequest request, CancellationToken ct = default)
            => RunWithProgressAsync(request, null, ct: ct);
        public async Task<ProviderRunResult> RunWithProgressAsync(ProviderRunRequest request,
            Func<ProviderProgressObservation, CancellationToken, ValueTask>? observe,
            DateTimeOffset? deadlineUtc = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            var started = DateTimeOffset.UtcNow;
            if (observe is not null) await observe(new ProviderProgressObservation
            { Phase = ProviderProgressPhase.RuntimeBinding, Sequence = 1, StartedAtUtc = started,
                ObservedAtUtc = started, DeadlineUtc = deadlineUtc!.Value }, ct);
            Observed.TrySetResult();
            try { await Release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) when (CooperativeCancellation)
            {
                return new ProviderRunResult { Provider = Profile.Id, Capability = request.Capability,
                    Operation = request.Operation, Mode = request.Mode, Status = ProviderRunStatus.Cancelled,
                    FailureClass = ProviderFailureClasses.Cancelled, ProviderStatus = "cancelled" };
            }
            return new ProviderRunResult { Provider = Profile.Id, Capability = request.Capability,
                Operation = request.Operation, Mode = request.Mode, Status = ProviderRunStatus.Succeeded,
                Output = new JsonObject { ["secret"] = "PRIVATE-OUTPUT" } };
        }
    }
}
