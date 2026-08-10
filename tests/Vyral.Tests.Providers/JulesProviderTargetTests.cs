using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Jules;

namespace Vyral.Tests.Providers;

public class JulesProviderTargetTests
{
    [Fact]
    public void JulesProvider_ExposesModePoliciesOnCapabilities()
    {
        var provider = new JulesProviderTarget(new JulesProviderOptions { ApiKey = "test-key", Source = "sources/repo-1" });

        Assert.All(provider.Capabilities, capability =>
            Assert.Contains(capability.ModePolicies, policy => policy.Id == "advisory" && policy.AllowNetwork));
    }

    [Fact]
    public async Task JulesProvider_ReportsDoctorChecksWithoutHttpExecution()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jules.test/v1alpha/") };
        var provider = new JulesProviderTarget(new JulesProviderOptions
        {
            ApiKey = "test-key",
            Source = "sources/repo-1",
            RequirePlanApproval = true
        }, client);

        var doctor = await provider.DiagnoseAsync();

        Assert.Equal("jules-api", doctor.Provider);
        Assert.Equal(ProviderDoctorStatuses.Warning, doctor.Status);
        Assert.Contains(doctor.Checks, check => check.Id == "auth.api_key" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(doctor.Checks, check => check.Id == "source.binding" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(doctor.Checks, check => check.Id == "lifecycle.surface" && check.Status == ProviderDoctorStatuses.Warning);
        Assert.Contains(doctor.Checks, check => check.Id == "qualification.probe" && check.Status == ProviderDoctorStatuses.Warning);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task JulesProvider_DoctorFailsWhenRequiredConfigIsMissing()
    {
        var provider = new JulesProviderTarget(new JulesProviderOptions());

        var doctor = await provider.DiagnoseAsync();

        Assert.Equal(ProviderDoctorStatuses.Failed, doctor.Status);
        Assert.Contains(doctor.Checks, check => check.Id == "auth.api_key" && check.Status == ProviderDoctorStatuses.Failed);
        Assert.Contains(doctor.Checks, check => check.Id == "source.binding" && check.Status == ProviderDoctorStatuses.Failed);
    }

    [Fact]
    public void JulesProvider_CreatesConservativeQualificationRequests()
    {
        var provider = new JulesProviderTarget(new JulesProviderOptions
        {
            ApiKey = "test-key",
            Source = "sources/repo-1",
            StartingBranch = "main"
        });

        var requests = provider.CreateQualificationRequests(new ProviderQualificationRequest
        {
            Capability = ProviderCapabilityIds.AgentJob
        });

        var request = Assert.Single(requests);
        Assert.Equal(ProviderCapabilityIds.AgentJob, request.Capability);
        Assert.Equal("createSession", request.Operation);
        Assert.Equal("mechanics", request.Mode);
        Assert.Equal("sources/repo-1", request.Payload["source"]?.GetValue<string>());
        Assert.Equal("main", request.Payload["startingBranch"]?.GetValue<string>());
        Assert.True(request.Payload["requirePlanApproval"]?.GetValue<bool>());
        Assert.Contains("qualification smoke", request.Payload["prompt"]?.GetValue<string>());
    }

    [Fact]
    public void JulesProvider_UsesConfiguredQualificationSessionProbe()
    {
        var provider = new JulesProviderTarget(new JulesProviderOptions
        {
            ApiKey = "test-key",
            Source = "sources/repo-1",
            QualificationSessionId = "sessions/session-smoke"
        });

        var requests = provider.CreateQualificationRequests(new ProviderQualificationRequest
        {
            Capability = ProviderCapabilityIds.AgentJob
        });

        var request = Assert.Single(requests);
        Assert.Equal(ProviderCapabilityIds.AgentJob, request.Capability);
        Assert.Equal("probeSession", request.Operation);
        Assert.Equal("mechanics", request.Mode);
        Assert.Equal("sessions/session-smoke", request.Payload["sessionId"]?.GetValue<string>());
        Assert.Null(request.Payload["prompt"]);
    }

    [Fact]
    public async Task CreateSession_PostsBoundaryPromptAndSourceContext()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"name\":\"sessions/session-1\"}", Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jules.test/v1alpha/") };
        var provider = new JulesProviderTarget(new JulesProviderOptions
        {
            ApiKey = "test-key",
            Source = "sources/repo-1",
            StartingBranch = "master"
        }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AgentJob,
            Operation = "createSession",
            Mode = "scaffold",
            Payload = new JsonObject
            {
                ["prompt"] = "Implement provider substrate.",
                ["title"] = "Provider substrate",
                ["requirePlanApproval"] = true
            }
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.EndsWith("/sessions", handler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.TryGetValues("X-Goog-Api-Key", out var values));
        Assert.Contains("test-key", values);
        Assert.Contains("Vyral provider boundary", handler.LastBody);
        Assert.Contains("\"source\":\"sources/repo-1\"", handler.LastBody);
        Assert.Contains("\"startingBranch\":\"master\"", handler.LastBody);
        Assert.Equal("sessions/session-1", result.Output["name"]?.GetValue<string>());
        var lifecycle = result.Output["jules"]!.AsObject();
        Assert.Equal(1, lifecycle["schemaVersion"]?.GetValue<int>());
        Assert.Equal("operationResponse", lifecycle["stateSource"]?.GetValue<string>());
        Assert.False(lifecycle["authoritativeSessionState"]?.GetValue<bool>());
        Assert.True(lifecycle["requiresSessionRefresh"]?.GetValue<bool>());
        Assert.Equal("getSession", lifecycle["sourceOfTruthOperation"]?.GetValue<string>());
        Assert.Equal("session-1", lifecycle["sessionId"]?.GetValue<string>());
        Assert.Contains("getSession", lifecycle["nextActions"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public async Task GetSession_NormalizesLifecycleQuestionsAndPullRequestFields()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "name": "sessions/session-1",
                  "state": "AWAITING_USER",
                  "pendingQuestions": [
                    { "question": "Approve the implementation plan?" }
                  ],
                  "pullRequest": {
                    "number": 42,
                    "htmlUrl": "https://github.com/example/repo/pull/42",
                    "headRef": "vyral/jules-session-1"
                  }
                }
                """, Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jules.test/v1alpha/") };
        var provider = new JulesProviderTarget(new JulesProviderOptions
        {
            ApiKey = "test-key",
            Source = "sources/repo-1"
        }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AgentJob,
            Operation = "getSession",
            Payload = new JsonObject { ["sessionId"] = "sessions/session-1" }
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        var lifecycle = result.Output["jules"]!.AsObject();
        Assert.Equal("session-1", lifecycle["sessionId"]?.GetValue<string>());
        Assert.Equal("AWAITING_USER", lifecycle["providerState"]?.GetValue<string>());
        Assert.Equal("session", lifecycle["stateSource"]?.GetValue<string>());
        Assert.True(lifecycle["authoritativeSessionState"]?.GetValue<bool>());
        Assert.False(lifecycle["requiresSessionRefresh"]?.GetValue<bool>());
        Assert.Equal("awaitingInput", lifecycle["lifecycleStatus"]?.GetValue<string>());
        Assert.True(lifecycle["requiresCallerAction"]?.GetValue<bool>());
        Assert.True(lifecycle["recoverable"]?.GetValue<bool>());
        Assert.Equal(1, lifecycle["pendingQuestionCount"]?.GetValue<int>());
        Assert.Equal("input", lifecycle["decisionRequired"]?.GetValue<string>());
        Assert.Equal("Approve the implementation plan?", lifecycle["pendingQuestions"]!.AsArray()[0]!.GetValue<string>());
        Assert.Contains("answerPendingQuestion", lifecycle["nextActions"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal("vyral/jules-session-1", lifecycle["headRef"]?.GetValue<string>());
        Assert.Equal("https://github.com/example/repo/pull/42", lifecycle["pullRequestUrl"]?.GetValue<string>());
        Assert.Equal(42, lifecycle["pullRequestNumber"]?.GetValue<int>());
        Assert.True(lifecycle["hasPullRequest"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GetSession_IgnoresResolvedQuestionsAndReadsSnakeCasePullRequestFields()
    {
        var result = await RunJulesGetSessionAsync("""
            {
              "name": "sessions/session-1",
              "state": "COMPLETED",
              "answeredQuestions": [
                { "question": "Already answered?", "answered": true }
              ],
              "pendingQuestions": [
                { "title": "Confirm final PR review?", "status": "open" }
              ],
              "pull_request": {
                "number": 51,
                "html_url": "https://github.com/example/repo/pull/51",
                "head_ref": "vyral/final-review"
              }
            }
            """);

        var lifecycle = result.Output["jules"]!.AsObject();
        Assert.Equal("awaitingInput", lifecycle["lifecycleStatus"]?.GetValue<string>());
        Assert.Equal(1, lifecycle["pendingQuestionCount"]?.GetValue<int>());
        Assert.Equal("Confirm final PR review?", lifecycle["pendingQuestions"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("vyral/final-review", lifecycle["headRef"]?.GetValue<string>());
        Assert.Equal("https://github.com/example/repo/pull/51", lifecycle["pullRequestUrl"]?.GetValue<string>());
        Assert.Equal(51, lifecycle["pullRequestNumber"]?.GetValue<int>());
    }

    [Fact]
    public async Task GetSession_DistinguishesRecoverableFailureAndPublishDecisionStates()
    {
        var recoverable = await RunJulesGetSessionAsync("""
            {
              "name": "sessions/session-1",
              "state": "FAILED_RECOVERABLE"
            }
            """);
        var recoverableLifecycle = recoverable.Output["jules"]!.AsObject();
        Assert.Equal("failedRecoverable", recoverableLifecycle["lifecycleStatus"]?.GetValue<string>());
        Assert.True(recoverableLifecycle["requiresCallerAction"]?.GetValue<bool>());
        Assert.True(recoverableLifecycle["recoverable"]?.GetValue<bool>());

        var publishDecision = await RunJulesGetSessionAsync("""
            {
              "name": "sessions/session-2",
              "state": "AWAITING_PUBLISH_DECISION"
            }
            """);
        var publishLifecycle = publishDecision.Output["jules"]!.AsObject();
        Assert.Equal("awaitingPublishDecision", publishLifecycle["lifecycleStatus"]?.GetValue<string>());
        Assert.True(publishLifecycle["requiresCallerAction"]?.GetValue<bool>());
        Assert.True(publishLifecycle["recoverable"]?.GetValue<bool>());
    }

    [Fact]
    public async Task ListActivities_NormalizesArrayResponses()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                [
                  { "type": "message", "text": "Working" },
                  { "type": "question", "question": "Need branch approval?" }
                ]
                """, Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jules.test/v1alpha/") };
        var provider = new JulesProviderTarget(new JulesProviderOptions
        {
            ApiKey = "test-key",
            Source = "sources/repo-1"
        }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AgentJob,
            Operation = "listActivities",
            Payload = new JsonObject { ["sessionId"] = "session-1" }
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(result.Output["items"]);
        var lifecycle = result.Output["jules"]!.AsObject();
        Assert.Equal("activities", lifecycle["stateSource"]?.GetValue<string>());
        Assert.False(lifecycle["authoritativeSessionState"]?.GetValue<bool>());
        Assert.Equal("awaitingInput", lifecycle["lifecycleStatus"]?.GetValue<string>());
        Assert.Equal(2, lifecycle["activityCount"]?.GetValue<int>());
        Assert.Equal(1, lifecycle["pendingQuestionCount"]?.GetValue<int>());
    }

    [Fact]
    public async Task JulesApi_NormalizesQuotaAndRateFailures()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("{\"error\":\"quota exceeded\"}", Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jules.test/v1alpha/") };
        var provider = new JulesProviderTarget(new JulesProviderOptions
        {
            ApiKey = "test-key",
            Source = "sources/repo-1"
        }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AgentJob,
            Operation = "createSession",
            Payload = new JsonObject { ["prompt"] = "Start job." }
        });

        Assert.Equal(ProviderRunStatus.Failed, result.Status);
        Assert.Equal(ProviderFailureClasses.Quota, result.FailureClass);
        Assert.Equal(ProviderFailureClasses.Quota, RequireTrace(result).FailureClass);
    }

    [Fact]
    public async Task JulesApi_FailsClosedOnOversizedOutput()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"name\":\"sessions/session-1\",\"large\":\"0123456789\"}", Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jules.test/v1alpha/") };
        var provider = new JulesProviderTarget(new JulesProviderOptions
        {
            ApiKey = "test-key",
            Source = "sources/repo-1"
        }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AgentJob,
            Operation = "createSession",
            MaxOutputBytes = 8,
            Payload = new JsonObject { ["prompt"] = "Start job." }
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Policy, result.FailureClass);
        Assert.Equal("output_limit", result.ProviderStatus);
        Assert.True(Encoding.UTF8.GetByteCount(result.Output["text"]?.GetValue<string>() ?? string.Empty) <= 8);
        Assert.True(result.Output["outputTruncated"]?.GetValue<bool>());
    }

    [Fact]
    public async Task JulesApi_RejectsUnknownModeBeforeHttpExecution()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jules.test/v1alpha/") };
        var provider = new JulesProviderTarget(new JulesProviderOptions
        {
            ApiKey = "test-key",
            Source = "sources/repo-1"
        }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AgentJob,
            Operation = "createSession",
            Mode = "unknown-mode",
            Payload = new JsonObject { ["prompt"] = "Start job." }
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Policy, result.FailureClass);
        Assert.Equal("unknown_mode", result.ProviderStatus);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task JulesApi_RequiresExplicitApiKey()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jules.test/v1alpha/") };
        var provider = new JulesProviderTarget(new JulesProviderOptions { Source = "sources/repo-1" }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AgentJob,
            Operation = "createSession",
            Payload = new JsonObject { ["prompt"] = "Start job." }
        });

        Assert.Equal(ProviderRunStatus.NotConfigured, result.Status);
        Assert.Equal(ProviderFailureClasses.Configuration, result.FailureClass);
        Assert.Null(handler.LastRequest);
    }

    private static async Task<ProviderRunResult> RunJulesGetSessionAsync(string responseJson)
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jules.test/v1alpha/") };
        var provider = new JulesProviderTarget(new JulesProviderOptions
        {
            ApiKey = "test-key",
            Source = "sources/repo-1"
        }, client);

        return await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AgentJob,
            Operation = "getSession",
            Payload = new JsonObject { ["sessionId"] = "sessions/session-1" }
        });
    }

    private static ProviderTraceEvent RequireTrace(ProviderRunResult result) =>
        Assert.IsType<ProviderTraceEvent>(result.Trace);

    private sealed class CapturingHttpHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public CapturingHttpHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return _response;
        }
    }
}
