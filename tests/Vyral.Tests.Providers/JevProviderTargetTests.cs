using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Jev;

namespace Vyral.Tests.Providers;

public class JevProviderTargetTests
{
    [Fact]
    public void JevProvider_ExposesModePoliciesOnCapabilities()
    {
        var provider = new JevProviderTarget(new JevProviderOptions { ApiKey = "test-key" });

        var capability = Assert.Single(provider.Capabilities);
        Assert.Equal(ProviderCapabilityIds.AiJudge, capability.Id);
        Assert.Contains(capability.ModePolicies, policy => policy.Id == "advisory" && policy.AllowNetwork);
        Assert.False(provider.Profile.Local);
        Assert.True(provider.Profile.RequiresNetwork);
        Assert.Equal(ProviderAuthTypes.ApiKey, provider.Profile.Auth);
    }

    [Fact]
    public async Task JevProvider_ReportsDoctorChecksWithoutHttpExecution()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jev.test/") };
        var provider = new JevProviderTarget(new JevProviderOptions { ApiKey = "test-key", ModelId = "jev-1.13.0" }, client);

        var doctor = await provider.DiagnoseAsync();

        Assert.Contains(doctor.Checks, check => check.Id == "auth.api_key" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(doctor.Checks, check => check.Id == "model.pin" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(doctor.Checks, check => check.Id == "integration.exercise" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Equal(ProviderDoctorStatuses.Ok, doctor.Status);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task JevProvider_DoctorWarnsOnUnpinnedRollingModelId()
    {
        var provider = new JevProviderTarget(new JevProviderOptions { ApiKey = "test-key", ModelId = "jev-latest" });

        var doctor = await provider.DiagnoseAsync();

        Assert.Contains(doctor.Checks, check => check.Id == "model.pin" && check.Status == ProviderDoctorStatuses.Warning);
    }

    [Fact]
    public async Task JevProvider_DoctorFailsWhenApiKeyMissing()
    {
        var provider = new JevProviderTarget(new JevProviderOptions());

        var doctor = await provider.DiagnoseAsync();

        Assert.Equal(ProviderDoctorStatuses.Failed, doctor.Status);
        Assert.Contains(doctor.Checks, check => check.Id == "auth.api_key" && check.Status == ProviderDoctorStatuses.Failed);
    }

    [Fact]
    public void JevProvider_CreatesConservativeQualificationSmokeRequest()
    {
        var provider = new JevProviderTarget(new JevProviderOptions { ApiKey = "test-key" });

        var requests = provider.CreateQualificationRequests(new ProviderQualificationRequest { Capability = ProviderCapabilityIds.AiJudge });

        var request = Assert.Single(requests);
        Assert.Equal("judge", request.Operation);
        Assert.Equal("mechanics", request.Mode);
        var payload = ProviderJson.DeserializePayload<AiJudgeRequest>(request);
        Assert.Single(payload.Questions);
        Assert.Equal(AiJudgeQuestionTypes.Noul, payload.Questions[0].Type);
    }

    [Fact]
    public async Task RunAsync_PostsToSystemOneEndpointWithBearerAuthAndMapsAnswers()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"model":"jev-1.13.0","answers":{"q1":{"type":"noul","noul":0.83}},"usage":{"input_tokens":40,"output_tokens":5}}""",
                Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jev.test/") };
        var provider = new JevProviderTarget(new JevProviderOptions { ApiKey = "test-key", ModelId = "jev-1.13.0" }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "judge",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "the invoice total is $412.50",
                Questions = new List<AiJudgeQuestion> { new() { Id = "q1", Type = AiJudgeQuestionTypes.Noul, Prompt = "is this over $400" } }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.EndsWith("/v1/systemone", handler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.True(handler.LastRequest!.Headers.TryGetValues("Authorization", out var values));
        Assert.Contains("Bearer test-key", values);
        Assert.Contains("\"model\":\"jev-1.13.0\"", handler.LastBody);
        Assert.Contains("\"state\":\"the invoice total is $412.50\"", handler.LastBody);
        Assert.Contains("\"q1\":{\"type\":\"noul\",\"instructions\":\"is this over $400\"}", handler.LastBody);

        Assert.Equal(AiValidationStatuses.ProviderJson, result.Output["validationStatus"]?.GetValue<string>());
        var answer = result.Output["answers"]!.AsArray()[0]!;
        Assert.Equal("q1", answer["questionId"]?.GetValue<string>());
        Assert.Equal(0.83, answer["probability"]?.GetValue<double>());
        Assert.Equal(0.83, answer["confidence"]?.GetValue<double>());
        Assert.False(answer["calibrated"]?.GetValue<bool>());
    }

    [Fact]
    public async Task RunAsync_SendsChoiceQuestionWithCriteriaMapAndMapsResponse()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"model":"jev-1.13.0","answers":{"q1":{"type":"choice","choice":"supports","probabilities":{"supports":0.9,"contradicts":0.05,"says_nothing":0.05},"confidence":0.9}}}""",
                Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jev.test/") };
        var provider = new JevProviderTarget(new JevProviderOptions { ApiKey = "test-key", ModelId = "jev-1.13.0" }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "judge",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "claim/section pair",
                Questions = new List<AiJudgeQuestion>
                {
                    new()
                    {
                        Id = "q1",
                        Type = AiJudgeQuestionTypes.Choice,
                        Prompt = "does the section support, contradict, or say nothing about the claim",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "supports", Label = "the section supports the claim" },
                            new() { Id = "contradicts", Label = "the section contradicts the claim" },
                            new() { Id = "says_nothing", Label = "the section says nothing about the claim" }
                        }
                    }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Contains("\"criteria\":{\"supports\":\"the section supports the claim\"", handler.LastBody);
        var answer = result.Output["answers"]!.AsArray()[0]!;
        Assert.Equal("supports", answer["choice"]?.GetValue<string>());
        Assert.Equal(0.9, answer["probabilities"]!["supports"]?.GetValue<double>());
        Assert.False(answer["calibrated"]?.GetValue<bool>());
    }

    [Fact]
    public async Task RunAsync_SendsScoreQuestionWithOrderedArrayCriteriaAndRemapsIndexKeyedResponse()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"model":"jev-1.13.0","answers":{"q1":{"type":"score","score":1.3,"confidence":0.54,"legend":{"0":"cosmetic","1":"degraded","2":"blocking"},"probabilities":{"0":0.0,"1":0.7,"2":0.3}}}}
                """,
                Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jev.test/") };
        var provider = new JevProviderTarget(new JevProviderOptions { ApiKey = "test-key", ModelId = "jev-1.13.0" }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "judge",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "the incident caused a two-hour outage",
                Questions = new List<AiJudgeQuestion>
                {
                    new()
                    {
                        Id = "q1",
                        Type = AiJudgeQuestionTypes.Score,
                        Prompt = "rate the severity",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "low", Label = "cosmetic" },
                            new() { Id = "medium", Label = "degraded" },
                            new() { Id = "high", Label = "blocking" }
                        }
                    }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        // criteria must be an ordered ARRAY of labels for Score, not the Choice-style id-keyed map.
        Assert.Contains("\"criteria\":[\"cosmetic\",\"degraded\",\"blocking\"]", handler.LastBody);

        var answer = result.Output["answers"]!.AsArray()[0]!;
        Assert.Equal(1.3, answer["score"]?.GetValue<double>());
        Assert.Equal(0.54, answer["confidence"]?.GetValue<double>());
        // Jev's raw response keys legend/probabilities by level index ("0","1","2"); this adapter must
        // remap them back to our own option ids using question.Options order.
        var legend = answer["legend"]!.AsObject();
        Assert.Equal("cosmetic", legend["low"]?.GetValue<string>());
        Assert.Equal("blocking", legend["high"]?.GetValue<string>());
        var probabilities = answer["probabilities"]!.AsObject();
        Assert.Equal(0.7, probabilities["medium"]?.GetValue<double>());
        Assert.False(answer["calibrated"]?.GetValue<bool>());
    }

    [Fact]
    public async Task RunAsync_RequiresExplicitApiKeyBeforeHttpExecution()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jev.test/") };
        var provider = new JevProviderTarget(new JevProviderOptions(), client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "judge",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Questions = new List<AiJudgeQuestion> { new() { Id = "q1", Type = AiJudgeQuestionTypes.Noul, Prompt = "x" } }
            })
        });

        Assert.Equal(ProviderRunStatus.NotConfigured, result.Status);
        Assert.Equal(ProviderFailureClasses.Configuration, result.FailureClass);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task RunAsync_RejectsEmptyQuestionsBeforeHttpExecution()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jev.test/") };
        var provider = new JevProviderTarget(new JevProviderOptions { ApiKey = "test-key" }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "judge",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest())
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Schema, result.FailureClass);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task RunAsync_RejectsUnknownModeBeforeHttpExecution()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jev.test/") };
        var provider = new JevProviderTarget(new JevProviderOptions { ApiKey = "test-key" }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "judge",
            Mode = "unknown-mode",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Questions = new List<AiJudgeQuestion> { new() { Id = "q1", Type = AiJudgeQuestionTypes.Noul, Prompt = "x" } }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Policy, result.FailureClass);
        Assert.Equal("unknown_mode", result.ProviderStatus);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task JevApi_ClassifiesAuthFailure()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"invalid api key\"}", Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jev.test/") };
        var provider = new JevProviderTarget(new JevProviderOptions { ApiKey = "bad-key" }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "judge",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Questions = new List<AiJudgeQuestion> { new() { Id = "q1", Type = AiJudgeQuestionTypes.Noul, Prompt = "x" } }
            })
        });

        Assert.Equal(ProviderRunStatus.Failed, result.Status);
        Assert.Equal(ProviderFailureClasses.Auth, result.FailureClass);
    }

    [Fact]
    public async Task JevApi_FailsClosedOnOversizedOutput()
    {
        var handler = new CapturingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"answers\":[{\"questionId\":\"q1\",\"probability\":0.5,\"large\":\"0123456789\"}]}", Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://jev.test/") };
        var provider = new JevProviderTarget(new JevProviderOptions { ApiKey = "test-key" }, client);

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "judge",
            MaxOutputBytes = 8,
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Questions = new List<AiJudgeQuestion> { new() { Id = "q1", Type = AiJudgeQuestionTypes.Noul, Prompt = "x" } }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Policy, result.FailureClass);
        Assert.Equal("output_limit", result.ProviderStatus);
        Assert.True(result.Output["outputTruncated"]?.GetValue<bool>());
    }

    [JevLiveFact]
    public async Task JevApi_LiveSmokeTestCoversAllThreeQuestionTypesAgainstTheRealEndpoint()
    {
        var apiKey = Environment.GetEnvironmentVariable("JEV_API_KEY") ?? Environment.GetEnvironmentVariable("TYPESAFE_API_TOKEN_KEY");
        var provider = new JevProviderTarget(new JevProviderOptions { ApiKey = apiKey, ModelId = "jev-1.13.0" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiJudge,
            Operation = "judge",
            Mode = "advisory",
            Payload = ProviderJson.ToJsonObject(new AiJudgeRequest
            {
                Context = "Vyral is an open-source contract layer and runtime; this is a one-time live smoke test of its new Jev provider adapter.",
                Questions = new List<AiJudgeQuestion>
                {
                    new() { Id = "smoke-1", Type = AiJudgeQuestionTypes.Noul, Prompt = "Is this text about an open-source software project?" },
                    new()
                    {
                        Id = "smoke-2",
                        Type = AiJudgeQuestionTypes.Choice,
                        Prompt = "What does this text primarily describe?",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "software_project", Label = "an open-source software project" },
                            new() { Id = "weather_forecast", Label = "a weather forecast" }
                        }
                    },
                    new()
                    {
                        Id = "smoke-3",
                        Type = AiJudgeQuestionTypes.Score,
                        Prompt = "how technical is this text",
                        Options = new List<AiJudgeOption>
                        {
                            new() { Id = "low", Label = "not technical at all, general audience" },
                            new() { Id = "medium", Label = "somewhat technical, some jargon" },
                            new() { Id = "high", Label = "highly technical, software-engineering-specific" }
                        }
                    }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal("jev-1.13.0", result.Output["modelId"]?.GetValue<string>());
        var answers = result.Output["answers"]!.AsArray();
        Assert.Equal(3, answers.Count);

        var noulAnswer = answers.Single(a => a!["questionId"]!.GetValue<string>() == "smoke-1")!;
        Assert.True(noulAnswer["probability"]!.GetValue<double>() > 0.5, "Expected a confident yes for an unambiguous claim about this repo.");

        var choiceAnswer = answers.Single(a => a!["questionId"]!.GetValue<string>() == "smoke-2")!;
        Assert.Equal("software_project", choiceAnswer["choice"]?.GetValue<string>());
        Assert.NotNull(choiceAnswer["probabilities"]);

        var scoreAnswer = answers.Single(a => a!["questionId"]!.GetValue<string>() == "smoke-3")!;
        Assert.NotNull(scoreAnswer["score"]);
        var legend = scoreAnswer["legend"]!.AsObject();
        Assert.Equal("highly technical, software-engineering-specific", legend["high"]?.GetValue<string>());
    }

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

public sealed class JevLiveFactAttribute : FactAttribute
{
    public JevLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JEV_API_KEY")) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPESAFE_API_TOKEN_KEY")))
        {
            Skip = "Set JEV_API_KEY or TYPESAFE_API_TOKEN_KEY to a live TypeSafe API key to run Jev live tests.";
        }
    }
}
