using System.Text.Json;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Cli;

namespace Vyral.Tests.Providers;

public partial class CliWorkspaceCodingAgentRunnerTests
{
    private static string HeldRecord(string reason) => JsonSerializer.Serialize(new
    {
        schemaVersion = "vyral.workspace-ipc-outcome.v1", state = "held", reason,
        resendAuthorized = false, providerEffects = "unresolved"
    });

    [BubblewrapFact]
    public async Task Diagnostic_PublicWorkerInputLimitAndExpiredDeadline_AreObservedWithoutGatewayOrWrites()
    {
        foreach (var reason in new[] { "input_limit", "deadline" })
        {
            await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../workers/workspace-ipc-agent/agent.py"));
            // Locate the source under either standard or external ArtifactsPath builds.
            source = Environment.GetEnvironmentVariable("VYRAL_TEST_WORKER_SOURCE") ?? source;
            Assert.True(File.Exists(source), "Set VYRAL_TEST_WORKER_SOURCE to the candidate public worker.");
            var worker = Path.Combine(fixture.Root, "worker.py");
            File.Copy(source, worker);
            var request = fixture.CreateRequest();
            var profile = new Dictionary<string, object?>
            {
                ["schemaVersion"] = "vyral.workspace-ipc-profile.v2", ["socketPath"] = "/missing-private/socket",
                ["credentialFile"] = "/missing-private/credential", ["sessionId"] = "synthetic-session",
                ["operationId"] = "synthetic-operation", ["contextRef"] = "synthetic-context",
                ["taskSha256"] = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Task))),
                ["baseCommit"] = fixture.BaseCommit, ["allowedPaths"] = request.AllowedPaths, ["avoidedPaths"] = request.AvoidedPaths,
                ["grantSha256"] = "sha256:" + new string('a',64), ["backendSha256"] = "sha256:" + new string('b',64),
                ["providerId"] = "synthetic-provider", ["modelId"] = "synthetic-model", ["contextFiles"] = Array.Empty<object>(),
                ["maxInputBytes"] = reason == "input_limit" ? 1 : 4096, ["maxOutputTokens"] = 128,
                ["maxReservationTokens"] = 8192, ["maxExecutionSeconds"] = 2, ["maxResponseBytes"] = 32768,
                ["maxSourceBytes"] = 8192, ["maxEdits"] = 4, ["maxEditBytes"] = 1024, ["allowInvoke"] = false,
                ["reservationRule"] = new { method = "utf8_bytes_plus_output", overhead_tokens = 2 }
            };
            if (reason == "deadline") profile["invocationDeadlineUtc"] = "2000-01-01T00:00:00Z";
            var profilePath = Path.Combine(fixture.Root,"profile.json");
            await File.WriteAllTextAsync(profilePath, JsonSerializer.Serialize(profile));
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(profilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var result = await fixture.CreateTarget(agentCommand:"/usr/bin/python3", agentArguments: new() { worker,"run","--profile",profilePath },
                inputFormat:"workspace-json-v1", promptTransport:CliPromptTransports.StandardInput, agentOutcomeFormat:"workspace-ipc-v1",
                agentReadOnlyPaths:new() { worker, profilePath }).RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(request,modelId:"synthetic-model"));
            await AssertUnchanged(fixture,result,"agent_process_failed");
            Assert.Equal(reason,result.Output["agentDiagnostic"]!["reason"]!.GetValue<string>());
            Assert.False(result.Output.ContainsKey("agentDelivery"));
            Assert.Equal(ProviderRunStatus.Failed,result.Status);
        }
    }

    [BubblewrapFact]
    public async Task Diagnostic_StrictStderrShape_UnknownAndPrivateValuesRemainGeneric()
    {
        var valid = HeldRecord("context_binding");
        var records = new[] {
            "", "not-json private-sentinel", "[]", "{}", HeldRecord("private-sentinel"),
            valid.Replace("\"held\"", "\"staged_for_host_validation\""), valid.Replace("false", "true"),
            valid.Replace("\"unresolved\"", "\"none\""), valid.Replace("\"reason\":", "\"reason\":\"deadline\",\"reason\":"),
            valid[..^1] + ",\"secret\":\"private-sentinel\"}", valid.Replace("\"context_binding\"","null"),
            valid.Replace("\"context_binding\"","[]"), valid + valid, "prefix " + valid, valid + " suffix",
            new string(' ',2049) + valid, valid.Replace("vyral.workspace-ipc-outcome.v1","other")
        };
        foreach (var record in records)
        {
            await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
            await File.WriteAllTextAsync(fixture.AgentScript,"#!/usr/bin/python3\nimport sys\nopen('/workspace/src/generated.txt','w').write('must-not-apply')\nprint(" + JsonSerializer.Serialize(record) + ",file=sys.stderr)\nprint('private-stdout-sentinel')\nraise SystemExit(20)\n");
            var result = await fixture.CreateTarget(inputFormat:"workspace-json-v1",promptTransport:CliPromptTransports.StandardInput,
                agentOutcomeFormat:"workspace-ipc-v1").RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(fixture.CreateRequest()));
            await AssertUnchanged(fixture,result,"agent_process_failed");
            Assert.False(result.Output.ContainsKey("agentDiagnostic"));
            Assert.False(result.Output.ContainsKey("agentDelivery"));
            Assert.DoesNotContain("sentinel",result.Output.ToJsonString());
            Assert.DoesNotContain("private",result.Error!);
        }
    }

    [BubblewrapFact]
    public async Task Diagnostic_StderrObservationDoesNotPromoteStdoutOrLegacyExit()
    {
        foreach (var mode in new[] { "valid", "stdout-only", "legacy", "exit1" })
        {
            await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
            var channel = mode == "stdout-only" ? "sys.stdout" : "sys.stderr";
            await File.WriteAllTextAsync(fixture.AgentScript,"#!/usr/bin/python3\nimport sys\nprint(" + JsonSerializer.Serialize(HeldRecord("context_binding")) + ",file="+channel+")\nraise SystemExit("+(mode == "exit1" ? 1 : 20)+")\n");
            var result = await fixture.CreateTarget(inputFormat:"workspace-json-v1",promptTransport:CliPromptTransports.StandardInput,
                agentOutcomeFormat:mode == "legacy" ? "none" : "workspace-ipc-v1").RunAsync(ProviderRunRequests.ForWorkspaceCodingAgent(fixture.CreateRequest()));
            await AssertUnchanged(fixture,result,"agent_process_failed");
            Assert.Equal(mode == "valid",result.Output.ContainsKey("agentDiagnostic"));
            Assert.False(result.Output.ContainsKey("agentDelivery"));
            if (mode == "valid")
            {
                var diagnostic = result.Output["agentDiagnostic"]!;
                Assert.Equal("agent_stderr",diagnostic["source"]!.GetValue<string>());
                Assert.False(diagnostic["resendAuthorized"]!.GetValue<bool>());
                Assert.Equal("unresolved",diagnostic["providerEffects"]!.GetValue<string>());
            }
        }
    }

    [BubblewrapFact]
    public async Task Diagnostic_ProcessFailuresKeepCancellationTimeoutOutputAndStartPrecedence()
    {
        foreach (var mode in new[] { "cancelled", "timeout", "output", "start" })
        {
            await using var fixture = await WorkspaceFixture.CreateAsync("delayed-write");
            var result = await fixture.CreateTarget(inputFormat:"workspace-json-v1",promptTransport:CliPromptTransports.StandardInput,
                agentOutcomeFormat:"workspace-ipc-v1",processRunner:new DiagnosticProcessRunner(mode)).RunAsync(
                    ProviderRunRequests.ForWorkspaceCodingAgent(fixture.CreateRequest()));
            Assert.Equal(mode switch { "cancelled" => "cancelled", "timeout" => "timeout", "output" => "output_limit", _ => "agent_process_failed" },result.ProviderStatus);
            Assert.False(result.Output.ContainsKey("agentDiagnostic"));
            Assert.False(result.Output.ContainsKey("agentDelivery"));
            Assert.False(File.Exists(Path.Combine(fixture.Workspace,"src/generated.txt")));
        }
    }

    private sealed class DiagnosticProcessRunner(string mode) : IProviderProcessRunner
    {
        public Task<ProviderProcessRunResult> RunAsync(ProviderProcessRunRequest request,CancellationToken ct = default) =>
            request.Command == BubblewrapTestEnvironment.Command ? Task.FromResult(new ProviderProcessRunResult {
                ExitCode=20, StandardError=HeldRecord("input_limit"), Cancelled=mode == "cancelled", TimedOut=mode == "timeout",
                OutputTruncated=mode == "output", StartError=mode == "start" ? "synthetic-start-error" : null
            }) : new SystemProviderProcessRunner().RunAsync(request,ct);
    }
}
