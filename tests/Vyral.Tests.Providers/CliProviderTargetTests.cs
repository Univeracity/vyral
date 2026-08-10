using System.Text.Json.Nodes;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Cli;

namespace Vyral.Tests.Providers;

public class CliProviderTargetTests
{
    [Fact]
    public void DefaultTargets_ExposeCapabilityProfilesWithoutSourceWriteAuthority()
    {
        var codex = CliProviderTargets.CreateCodex(new CapturingProcessRunner());
        var claude = CliProviderTargets.CreateClaude(new CapturingProcessRunner());
        var gemini = CliProviderTargets.CreateGemini(new CapturingProcessRunner());
        var antigravity = CliProviderTargets.CreateAntigravity(new CapturingProcessRunner());
        var grokBuild = CliProviderTargets.CreateGrokBuild(new CapturingProcessRunner());

        Assert.Contains(ProviderCapabilityIds.AiScaffold, codex.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiRerank, codex.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiExtract, codex.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiToolPlan, codex.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiExtract, claude.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiReview, gemini.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiToolPlan, gemini.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiScaffold, antigravity.Capabilities.Select(c => c.Id));
        Assert.Contains(ProviderCapabilityIds.AiReview, grokBuild.Capabilities.Select(c => c.Id));
        Assert.All(grokBuild.Capabilities, capability => Assert.Contains("source_writes", capability.UnsupportedFeatures));
        Assert.Equal("grok-build", grokBuild.Profile.Family);
        Assert.All(claude.Capabilities, capability => Assert.Contains("source_writes", capability.UnsupportedFeatures));
        Assert.True(codex.Profile.Local);
        Assert.True(codex.Profile.RequiresNetwork);
        Assert.Contains(codex.Capabilities.SelectMany(capability => capability.ModePolicies), policy => policy.Id == "advisory" && policy.AllowNetwork);
        Assert.Contains(codex.Capabilities.SelectMany(capability => capability.ModePolicies), policy =>
            policy.Id == ProviderModes.Research &&
            policy.AllowNetwork &&
            policy.ToolPolicy == ProviderToolPolicies.ProviderOwned &&
            !policy.AllowSourceWrites);
    }

    [Fact]
    public void CliProvider_CreatesOptInQualificationSmokeRequests()
    {
        var provider = CliProviderTargets.CreateCodex(new CapturingProcessRunner());

        var requests = provider.CreateQualificationRequests(new ProviderQualificationRequest
        {
            Capability = ProviderCapabilityIds.AiReview,
            Mode = "mechanics"
        });

        var request = Assert.Single(requests);
        Assert.Equal(ProviderCapabilityIds.AiReview, request.Capability);
        Assert.Equal("run", request.Operation);
        Assert.Equal("mechanics", request.Mode);
        Assert.Equal(30, request.TimeoutSeconds);
        Assert.Equal(4096, request.MaxOutputBytes);
        Assert.Contains("qualification smoke", request.Payload["prompt"]?.GetValue<string>());

        var extract = Assert.Single(provider.CreateQualificationRequests(new ProviderQualificationRequest
        {
            Capability = ProviderCapabilityIds.AiExtract,
            Mode = "mechanics"
        }));
        Assert.Equal(ProviderCapabilityIds.AiExtract, extract.Capability);
        Assert.Contains("qualification smoke", extract.Payload["text"]?.GetValue<string>());
        Assert.Contains("Return JSON only", extract.Payload["instructions"]?.GetValue<string>());

        var toolPlan = Assert.Single(provider.CreateQualificationRequests(new ProviderQualificationRequest
        {
            Capability = ProviderCapabilityIds.AiToolPlan,
            Mode = "mechanics"
        }));
        Assert.Equal(ProviderCapabilityIds.AiToolPlan, toolPlan.Capability);
        Assert.Equal("noop", toolPlan.Payload["tools"]!.AsArray()[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task CodexRun_UsesSparkModelByDefault()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "Chat response." }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Payload = new JsonObject { ["prompt"] = "Answer quickly." }
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal("codex-test", runner.LastRequest!.Command);
        Assert.Equal("exec", runner.LastRequest.Arguments[0]);
        Assert.Equal("-m", runner.LastRequest.Arguments[1]);
        Assert.Equal(CliProviderTargets.DefaultCodexModelId, runner.LastRequest.Arguments[2]);
        Assert.Equal("-", runner.LastRequest.Arguments.Last());
        Assert.DoesNotContain(runner.LastRequest.Arguments, argument => argument.Contains("Answer quickly.", StringComparison.Ordinal));
        Assert.Contains("Answer quickly.", runner.LastRequest.StandardInput);
        Assert.Equal(CliProviderTargets.DefaultCodexModelId, RequireTrace(result).ModelId);
    }

    [Fact]
    public async Task CodexResearchMode_PermitsOnlyReadOnlyPublicWebResearch()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "Current answer." }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Mode = ProviderModes.Research,
            Payload = new JsonObject { ["prompt"] = "Verify the current event before answering." }
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Contains("provider-managed public-web research tool", runner.LastRequest!.StandardInput);
        Assert.Contains("Do not inspect a workspace, filesystem, database", runner.LastRequest.StandardInput);
        Assert.Contains("do not execute commands or write sources", runner.LastRequest.StandardInput);
        Assert.DoesNotContain("Tool execution remains caller-owned", runner.LastRequest.StandardInput);
    }

    [Fact]
    public async Task CodexRun_AllowsRequestLevelModelOverride()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "Full Codex response." }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            ModelId = CliProviderTargets.CodexModelId,
            Payload = new JsonObject { ["prompt"] = "Use full Codex for this request." }
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal(CliProviderTargets.CodexModelId, runner.LastRequest?.Arguments[2]);
        Assert.Equal(CliProviderTargets.CodexModelId, RequireTrace(result).ModelId);
        Assert.NotEqual(provider.Profile.ConfigHash, RequireTrace(result).ConfigHash);

        var blankResult = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            ModelId = " ",
            Payload = new JsonObject { ["prompt"] = "Use the configured default." }
        });

        Assert.Equal(ProviderRunStatus.Succeeded, blankResult.Status);
        Assert.Equal(CliProviderTargets.DefaultCodexModelId, runner.LastRequest?.Arguments[2]);
        Assert.Equal(CliProviderTargets.DefaultCodexModelId, RequireTrace(blankResult).ModelId);
        Assert.Equal(provider.Profile.ConfigHash, RequireTrace(blankResult).ConfigHash);
    }

    [Fact]
    public async Task CodexRun_AllowsExplicitModelOverrideAndIgnoresBlankOverride()
    {
        var explicitRunner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "Explicit model response." }
        };
        var explicitProvider = CliProviderTargets.CreateCodex(explicitRunner, new CliProviderOptions
        {
            Command = "codex-test",
            ModelId = "gpt-5.3-codex"
        });

        var explicitResult = await explicitProvider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Payload = new JsonObject { ["prompt"] = "Use the configured model." }
        });

        var blankRunner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "Blank model response." }
        };
        var blankProvider = CliProviderTargets.CreateCodex(blankRunner, new CliProviderOptions
        {
            Command = "codex-test",
            ModelId = " "
        });

        var blankResult = await blankProvider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Payload = new JsonObject { ["prompt"] = "Use the default model." }
        });

        Assert.Equal(ProviderRunStatus.Succeeded, explicitResult.Status);
        Assert.Equal("gpt-5.3-codex", explicitRunner.LastRequest?.Arguments[2]);
        Assert.Contains("Use the configured model.", explicitRunner.LastRequest?.StandardInput);
        Assert.Equal("gpt-5.3-codex", RequireTrace(explicitResult).ModelId);
        Assert.Equal(ProviderRunStatus.Succeeded, blankResult.Status);
        Assert.Equal(CliProviderTargets.DefaultCodexModelId, blankRunner.LastRequest?.Arguments[2]);
        Assert.Contains("Use the default model.", blankRunner.LastRequest?.StandardInput);
        Assert.Equal(CliProviderTargets.DefaultCodexModelId, RequireTrace(blankResult).ModelId);
    }

    [Fact]
    public async Task CodexModelCatalog_ExposesDefaultAndConfiguredModels()
    {
        var provider = CliProviderTargets.CreateCodex(new CapturingProcessRunner(), new CliProviderOptions
        {
            KnownModels = new List<string> { "gpt-5.3-codex-experimental" }
        });

        var catalog = await provider.ListModelsAsync();

        Assert.Equal("codex-cli", catalog.Provider);
        Assert.Equal(ProviderModelCatalogStatuses.Succeeded, catalog.Status);
        Assert.Equal("configured-static", catalog.Source);
        Assert.Equal(CliProviderTargets.DefaultCodexModelId, catalog.DefaultModelId);
        Assert.Equal(new[] { CliProviderTargets.DefaultCodexModelId, CliProviderTargets.CodexModelId, "gpt-5.3-codex-experimental" }, catalog.Items.Select(model => model.Id));
        var spark = catalog.Items.Single(model => model.Id == CliProviderTargets.DefaultCodexModelId);
        Assert.True(spark.Default);
        var unsupportedTools = Assert.IsType<string[]>(spark.Metadata["knownUnsupportedTools"]);
        Assert.Equal(new[] { "image_generation" }, unsupportedTools);
        Assert.Equal("limited", spark.Metadata["toolCompatibility"]);
        Assert.False(catalog.Items.Single(model => model.Id == CliProviderTargets.CodexModelId).Metadata.ContainsKey("knownUnsupportedTools"));
        Assert.All(catalog.Items, model => Assert.Contains(ProviderCapabilityIds.AiChat, model.Capabilities));
        Assert.All(catalog.Items, model => Assert.Contains(ProviderCapabilityIds.AiExtract, model.Capabilities));
    }

    [Fact]
    public async Task CodexQuota_UsesWebSocketAppServerByDefaultAndNormalizesBuckets()
    {
        var quotaClient = new CapturingCodexAppServerQuotaClient
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 0,
                StandardOutput = """
                    {"id":0,"result":{"userAgent":"vyral-test"}}
                    {"id":1,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":52,"windowDurationMins":300,"resetsAt":1779270900},"secondary":{"usedPercent":27,"windowDurationMins":10080,"resetsAt":1779820538},"credits":{"hasCredits":false,"unlimited":false,"balance":"0"},"planType":"prolite","rateLimitReachedType":null},"rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":52,"windowDurationMins":300,"resetsAt":1779270900},"secondary":{"usedPercent":27,"windowDurationMins":10080,"resetsAt":1779820538},"credits":{"hasCredits":false,"unlimited":false,"balance":"0"},"planType":"prolite","rateLimitReachedType":null},"codex_bengalfox":{"limitId":"codex_bengalfox","limitName":"GPT-5.3-Codex-Spark","primary":{"usedPercent":5,"windowDurationMins":300,"resetsAt":1779275122},"secondary":{"usedPercent":2,"windowDurationMins":10080,"resetsAt":1779822716},"credits":null,"planType":"prolite","rateLimitReachedType":null}}}}
                    """
            }
        };
        var runner = new CapturingProcessRunner();
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" }, quotaClient);

        var quota = await provider.GetQuotaAsync();

        Assert.Null(runner.LastRequest);
        Assert.NotNull(quotaClient.LastRequest);
        Assert.True(quotaClient.LastRequest!.AutoStartWebSocket);
        Assert.Equal("codex", quotaClient.LastRequest.Command);
        Assert.Equal(new[] { "app-server", "--listen", "ws://127.0.0.1:0" }, quotaClient.LastRequest.LaunchArguments);
        Assert.Equal("codex-cli", quota.Provider);
        Assert.Equal(ProviderQuotaStatuses.Succeeded, quota.Status);
        Assert.Equal(CliQuotaSources.CodexAppServerWebSocket, quota.Source);
        Assert.Equal(new[] { "codex", "codex_bengalfox" }, quota.Items.Select(item => item.LimitId));
        var codex = quota.Items.Single(item => item.LimitId == "codex");
        Assert.Equal(52, codex.Primary?.UsedPercent);
        Assert.Equal(48, codex.Primary?.RemainingPercent);
        Assert.Equal(300, codex.Primary?.WindowDurationMins);
        Assert.Equal("prolite", codex.PlanType);
        Assert.Equal("0", codex.Credits?["balance"]?.GetValue<string>());
        var spark = quota.Items.Single(item => item.LimitId == "codex_bengalfox");
        Assert.Equal("GPT-5.3-Codex-Spark", spark.LimitName);
        Assert.Equal(5, spark.Primary?.UsedPercent);
    }

    [Fact]
    public async Task CodexQuota_UsesConfiguredWebSocketUri()
    {
        var quotaClient = new CapturingCodexAppServerQuotaClient
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 0,
                StandardOutput = "{\"id\":1,\"result\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":1}}}}"
            }
        };
        var provider = CliProviderTargets.CreateCodex(new CapturingProcessRunner(), new CliProviderOptions
        {
            Command = "codex-test",
            QuotaWebSocketUri = "ws://127.0.0.1:48765",
            QuotaAutoStartWebSocket = false
        }, quotaClient);

        var quota = await provider.GetQuotaAsync();

        Assert.NotNull(quotaClient.LastRequest);
        Assert.Equal(new Uri("ws://127.0.0.1:48765"), quotaClient.LastRequest!.WebSocketUri);
        Assert.False(quotaClient.LastRequest.AutoStartWebSocket);
        Assert.Equal(ProviderQuotaStatuses.Succeeded, quota.Status);
        Assert.Equal(CliQuotaSources.CodexAppServerWebSocket, quota.Source);
    }

    [Fact]
    public async Task CodexQuota_QueriesAppServerProxyWhenConfiguredAndNormalizesBuckets()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 0,
                StandardOutput = """
                    {"method":"account/rateLimits/updated","params":{"rateLimits":{"limitId":"codex"}}}
                    {"id":1,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":15,"resetsAt":1730947200},"secondary":null,"rateLimitReachedType":null},"rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":15,"resetsAt":1730947200},"secondary":null,"rateLimitReachedType":null},"codex_other":{"limitId":"codex_other","limitName":"codex_other","primary":{"usedPercent":42,"windowDurationMins":60,"resetsAt":1730950800},"secondary":null,"rateLimitReachedType":"soft_limit"}}}}
                    """
            }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions
        {
            Command = "codex-test",
            QuotaSource = CliQuotaSources.CodexAppServerProxy
        });

        var quota = await provider.GetQuotaAsync();

        Assert.NotNull(runner.LastRequest);
        Assert.Equal("codex", runner.LastRequest!.Command);
        Assert.Equal(new[] { "app-server", "proxy" }, runner.LastRequest.Arguments);
        Assert.Contains("account/rateLimits/read", runner.LastRequest.StandardInput);
        Assert.Equal("codex-cli", quota.Provider);
        Assert.Equal(ProviderQuotaStatuses.Succeeded, quota.Status);
        Assert.Equal(CliQuotaSources.CodexAppServerProxy, quota.Source);
        Assert.True(quota.Advisory);
        Assert.Equal(new[] { "codex", "codex_other" }, quota.Items.Select(item => item.LimitId));
        var codex = quota.Items.Single(item => item.LimitId == "codex");
        Assert.Equal(25, codex.Primary?.UsedPercent);
        Assert.Equal(75, codex.Primary?.RemainingPercent);
        Assert.Equal(15, codex.Primary?.WindowDurationMins);
        Assert.Equal(1730947200, codex.Primary?.ResetsAtUnixSeconds);
        Assert.NotNull(codex.Primary?.ResetsAt);
        var other = quota.Items.Single(item => item.LimitId == "codex_other");
        Assert.Equal("soft_limit", other.RateLimitReachedType);
        Assert.Equal(42, other.Primary?.UsedPercent);
    }

    [Fact]
    public async Task CliQuota_ReturnsUnsupportedWhenNoQuotaSourceIsConfigured()
    {
        var provider = CliProviderTargets.CreateClaude(new CapturingProcessRunner());

        var quota = await provider.GetQuotaAsync();

        Assert.Equal("claude-cli", quota.Provider);
        Assert.Equal(ProviderQuotaStatuses.Unsupported, quota.Status);
        Assert.Equal(ProviderFailureClasses.Unsupported, quota.FailureClass);
        Assert.Empty(quota.Items);
    }

    [Fact]
    public async Task CodexQuota_ReturnsUnavailableWhenAppServerProxyFails()
    {
        var provider = CliProviderTargets.CreateCodex(new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 2,
                StandardError = "app-server socket not found"
            }
        }, new CliProviderOptions
        {
            QuotaSource = CliQuotaSources.CodexAppServerProxy
        });

        var quota = await provider.GetQuotaAsync();

        Assert.Equal(ProviderQuotaStatuses.Unavailable, quota.Status);
        Assert.Equal(ProviderFailureClasses.ProviderUnavailable, quota.FailureClass);
        Assert.Equal("2", quota.ProviderStatus);
        Assert.Equal("app-server socket not found", quota.Metadata["stderr"]);
    }

    [Fact]
    public async Task GeminiRun_UsesFlashLiteModelByDefault()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "Gemini response." }
        };
        var provider = CliProviderTargets.CreateGemini(runner, new CliProviderOptions { Command = "gemini-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Payload = new JsonObject { ["prompt"] = "Answer quickly." }
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal("gemini-test", runner.LastRequest!.Command);
        Assert.Equal("--model", runner.LastRequest.Arguments[0]);
        Assert.Equal(CliProviderTargets.DefaultGeminiModelId, runner.LastRequest.Arguments[1]);
        Assert.Contains("Answer quickly.", runner.LastRequest.Arguments.Last());
        Assert.Equal(CliProviderTargets.DefaultGeminiModelId, RequireTrace(result).ModelId);
    }

    [Fact]
    public async Task GeminiModelCatalog_ExposesFlashLiteDefaultAndFlashFallback()
    {
        var provider = CliProviderTargets.CreateGemini(new CapturingProcessRunner());

        var catalog = await provider.ListModelsAsync();

        Assert.Equal("gemini-cli", catalog.Provider);
        Assert.Equal(ProviderModelCatalogStatuses.Succeeded, catalog.Status);
        Assert.Equal(CliProviderTargets.DefaultGeminiModelId, catalog.DefaultModelId);
        Assert.Equal(9, catalog.Items.Count);
        var ids = catalog.Items.Select(model => model.Id).ToList();
        Assert.Contains(CliProviderTargets.DefaultGeminiModelId, ids);
        Assert.Contains(CliProviderTargets.GeminiFlashModelId, ids);
        Assert.Contains(CliProviderTargets.GeminiProModelId, ids);
        Assert.Contains(CliProviderTargets.Gemini3FlashPreviewModelId, ids);
        Assert.Contains(CliProviderTargets.Gemini3ProPreviewModelId, ids);
        Assert.Contains(CliProviderTargets.Gemini31ProPreviewModelId, ids);
        Assert.Contains(CliProviderTargets.Gemini31FlashLitePreviewModelId, ids);
        Assert.Contains(CliProviderTargets.Gemma4_31BModelId, ids);
        Assert.Contains(CliProviderTargets.Gemma4_26BMoeModelId, ids);
        Assert.True(catalog.Items.Single(model => model.Id == CliProviderTargets.DefaultGeminiModelId).Default);
        Assert.All(catalog.Items, model => Assert.Contains(ProviderCapabilityIds.AiChat, model.Capabilities));
        var pro25Entry = catalog.Items.Single(model => model.Id == CliProviderTargets.GeminiProModelId);
        Assert.Equal("pro", pro25Entry.Metadata["tier"]?.ToString());
        Assert.Equal("2.5", pro25Entry.Metadata["generation"]?.ToString());
        var pro3Entry = catalog.Items.Single(model => model.Id == CliProviderTargets.Gemini3ProPreviewModelId);
        Assert.Equal("pro", pro3Entry.Metadata["tier"]?.ToString());
        Assert.Equal("3.0", pro3Entry.Metadata["generation"]?.ToString());
        Assert.Equal(true.ToString(), pro3Entry.Metadata["preview"]?.ToString());
        var gemmaEntry = catalog.Items.Single(model => model.Id == CliProviderTargets.Gemma4_31BModelId);
        Assert.Equal("gemma", gemmaEntry.Metadata["family"]?.ToString());
    }

    [Fact]
    public async Task GeminiRun_AcceptsTypedExtractPayload()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 0,
                StandardOutput = "{\"draftCopy\":\"Soft cotton crib sheet\",\"backendTerms\":[\"cotton crib sheet\"]}"
            }
        };
        var provider = CliProviderTargets.CreateGemini(runner, new CliProviderOptions { Command = "gemini-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiExtract,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiExtractRequest
            {
                Text = "Product: cotton crib sheet. Customer search terms mention soft breathable fabric.",
                Instructions = "Extract product listing copy fields for review.",
                Schema = new JsonObject
                {
                    ["draftCopy"] = "string",
                    ["backendTerms"] = new JsonArray(JsonValue.Create("string"))
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal("gemini-test", runner.LastRequest!.Command);
        Assert.Equal(CliProviderTargets.DefaultGeminiModelId, runner.LastRequest.Arguments[1]);
        Assert.Contains("Extract structured information", runner.LastRequest.Arguments.Last());
        Assert.Contains("Return a JSON object only.", runner.LastRequest.Arguments.Last());
        Assert.Contains("Extract product listing copy fields for review.", runner.LastRequest.Arguments.Last());
        Assert.Equal(CliProviderTargets.DefaultGeminiModelId, RequireTrace(result).ModelId);
        Assert.Equal("Soft cotton crib sheet", result.Output["data"]?["draftCopy"]?.GetValue<string>());
        Assert.Equal("cotton crib sheet", result.Output["data"]?["backendTerms"]?[0]?.GetValue<string>());
        Assert.Equal("provider_json", result.Output["validationStatus"]?.GetValue<string>());
    }

    [Fact]
    public async Task CliDoctor_ReportsMissingCommandAndPromptTransport()
    {
        var provider = CliProviderTargets.CreateGemini(new CapturingProcessRunner(), new CliProviderOptions
        {
            Command = "vyral-missing-gemini-command",
            ModelId = "gemini-2.5-flash-lite"
        });

        var doctor = await provider.DiagnoseAsync();

        Assert.Equal("gemini-cli", doctor.Provider);
        Assert.Equal(ProviderDoctorStatuses.Failed, doctor.Status);
        Assert.Contains(doctor.Checks, check => check.Id == "command.resolution" && check.Status == ProviderDoctorStatuses.Failed);
        Assert.Contains(doctor.Checks, check => check.Id == "model.binding" && check.Status == ProviderDoctorStatuses.Ok);
        Assert.Contains(doctor.Checks, check => check.Id == "prompt.transport" && check.Status == ProviderDoctorStatuses.Warning);
    }

    [Fact]
    public async Task ClaudeRun_BindsConfiguredModelAndCatalog()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "{\"summary\":\"ok\"}" }
        };
        var provider = CliProviderTargets.CreateClaude(runner, new CliProviderOptions
        {
            Command = "claude-test",
            ModelId = "sonnet"
        });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiReview,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiReviewRequest { Prompt = "Review this change." })
        });
        var catalog = await provider.ListModelsAsync();

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Contains("--model", runner.LastRequest!.Arguments);
        Assert.Contains("sonnet", runner.LastRequest.Arguments);
        Assert.Equal("sonnet", RequireTrace(result).ModelId);
        Assert.Equal(ProviderModelCatalogStatuses.Succeeded, catalog.Status);
        Assert.Equal("sonnet", catalog.DefaultModelId);
        Assert.Contains("sonnet", catalog.Items.Select(model => model.Id));
        Assert.True(catalog.Items.Single(model => model.Id == "sonnet").Default);
    }

    [Fact]
    public async Task AntigravityRun_UsesFlashLiteModelByDefaultWithoutModelArg()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "Antigravity response." }
        };
        var provider = CliProviderTargets.CreateAntigravity(runner, new CliProviderOptions { Command = "agy-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Payload = new JsonObject { ["prompt"] = "Answer quickly." }
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal("agy-test", runner.LastRequest!.Command);
        Assert.Equal("--print", runner.LastRequest.Arguments[0]);
        Assert.Contains("Answer quickly.", runner.LastRequest.Arguments[1]);
        Assert.DoesNotContain("--model", runner.LastRequest.Arguments);
        Assert.Equal(CliProviderTargets.DefaultGeminiModelId, RequireTrace(result).ModelId);
    }

    [Fact]
    public async Task GrokBuildRun_UsesPromptFileAndConfiguredContainment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vyral-grok-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var prompts = Path.Combine(workspace, "prompts");
        var grokHome = Path.Combine(root, "grok-home");
        var sentinel = Path.Combine(root, "outside-workspace-sentinel.txt");
        Directory.CreateDirectory(prompts);
        Directory.CreateDirectory(grokHome);
        await File.WriteAllTextAsync(sentinel, "must-not-be-mounted-or-passed-to-grok");
        string? promptPath = null;
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "Grok advisory response." },
            OnRun = request =>
            {
                promptPath = request.Arguments.SkipWhile(argument => argument != "--prompt-file").Skip(1).FirstOrDefault();
                Assert.NotNull(promptPath);
                Assert.StartsWith(Path.GetFullPath(prompts) + Path.DirectorySeparatorChar, Path.GetFullPath(promptPath!));
                Assert.Contains("Review this design.", File.ReadAllText(promptPath!));
                Assert.DoesNotContain(request.Arguments, argument => argument.Contains("Review this design.", StringComparison.Ordinal));
                Assert.DoesNotContain(sentinel, request.Arguments);
                Assert.Equal(workspace, request.WorkingDirectory);
                Assert.True(request.ClearEnvironment);
                Assert.Equal(new[] { "HOME" }, request.Environment.Keys);
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(promptPath!));
                }
            }
        };
        var provider = CliProviderTargets.CreateGrokBuild(runner, new GrokBuildProviderOptions
        {
            Command = typeof(CliProviderTarget).Assembly.Location,
            ModelId = "grok-build-configured",
            WorkingDirectory = workspace,
            PromptFileDirectory = prompts,
            Environment = new Dictionary<string, string?> { ["HOME"] = grokHome },
            SandboxProfile = "vyral-advisory",
            ToolDenyRules = new List<string> { "shell:*" }
        });

        try
        {
            var result = await provider.RunAsync(new ProviderRunRequest
            {
                Capability = ProviderCapabilityIds.AiChat,
                Operation = "run",
                Payload = new JsonObject { ["prompt"] = "Review this design." }
            });

            Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
            Assert.NotNull(runner.LastRequest);
            Assert.Equal(typeof(CliProviderTarget).Assembly.Location, runner.LastRequest!.Command);
            Assert.Contains("--prompt-file", runner.LastRequest.Arguments);
            Assert.DoesNotContain("--single", runner.LastRequest.Arguments);
            Assert.Contains("--sandbox", runner.LastRequest.Arguments);
            Assert.Contains("vyral-advisory", runner.LastRequest.Arguments);
            Assert.Contains("--deny", runner.LastRequest.Arguments);
            Assert.Contains("shell:*", runner.LastRequest.Arguments);
            Assert.Contains("--model", runner.LastRequest.Arguments);
            Assert.Contains("grok-build-configured", runner.LastRequest.Arguments);
            Assert.Contains("--permission-mode", runner.LastRequest.Arguments);
            Assert.Contains("plan", runner.LastRequest.Arguments);
            Assert.Contains("--disable-web-search", runner.LastRequest.Arguments);
            Assert.Contains("--no-subagents", runner.LastRequest.Arguments);
            Assert.Contains("--no-memory", runner.LastRequest.Arguments);
            Assert.Null(runner.LastRequest.StandardInput);
            Assert.Equal("grok-build-configured", RequireTrace(result).ModelId);
            Assert.Equal(ProviderToolPolicies.ProviderOwned, provider.Capabilities[0].ToolPolicy);
            Assert.NotNull(promptPath);
            Assert.False(File.Exists(promptPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GrokBuildDoctor_FailsClosedWithoutConfiguredContainment()
    {
        var provider = CliProviderTargets.CreateGrokBuild(new CapturingProcessRunner());

        var doctor = await provider.DiagnoseAsync();

        Assert.Equal(ProviderDoctorStatuses.Failed, doctor.Status);
        var containment = Assert.Single(doctor.Checks, check => check.Id == "execution.containment");
        Assert.Equal(ProviderDoctorStatuses.Failed, containment.Status);
        Assert.Contains("sandbox profile", containment.Message, StringComparison.Ordinal);
        Assert.Contains("deny rule", containment.Message, StringComparison.Ordinal);
        Assert.Contains(doctor.Checks, check => check.Id == "prompt.transport" && check.Status == ProviderDoctorStatuses.Ok);
    }

    [Fact]
    public async Task GrokBuildRun_RejectsAWorkspaceContainingSourceData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vyral-grok-source-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var prompts = Path.Combine(workspace, "prompts");
        var grokHome = Path.Combine(root, "grok-home");
        Directory.CreateDirectory(prompts);
        Directory.CreateDirectory(grokHome);
        await File.WriteAllTextAsync(Path.Combine(workspace, "consumer-source-sentinel.txt"), "do not expose this");
        var runner = new CapturingProcessRunner();
        var provider = CliProviderTargets.CreateGrokBuild(runner, new GrokBuildProviderOptions
        {
            Command = typeof(CliProviderTarget).Assembly.Location,
            WorkingDirectory = workspace,
            PromptFileDirectory = prompts,
            Environment = new Dictionary<string, string?> { ["HOME"] = grokHome },
            SandboxProfile = "vyral-advisory",
            ToolDenyRules = new List<string> { "shell:*" }
        });

        try
        {
            var result = await provider.RunAsync(new ProviderRunRequest
            {
                Capability = ProviderCapabilityIds.AiChat,
                Operation = "run",
                Payload = new JsonObject { ["prompt"] = "Review this design." }
            });

            Assert.Equal(ProviderRunStatus.Rejected, result.Status);
            Assert.Equal(ProviderFailureClasses.Policy, result.FailureClass);
            Assert.Equal("containment_preflight_failed", result.ProviderStatus);
            Assert.Null(runner.LastRequest);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GrokBuildExecutableReplacement_InvalidatesDoctorAndQualificationConfigHash()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vyral-grok-identity-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var prompts = Path.Combine(workspace, "prompts");
        var grokHome = Path.Combine(root, "grok-home");
        var command = Path.Combine(root, "grok-bound-command");
        Directory.CreateDirectory(prompts);
        Directory.CreateDirectory(grokHome);
        File.Copy(typeof(CliProviderTarget).Assembly.Location, command);
        var options = new GrokBuildProviderOptions
        {
            Command = command,
            WorkingDirectory = workspace,
            PromptFileDirectory = prompts,
            Environment = new Dictionary<string, string?> { ["HOME"] = grokHome },
            SandboxProfile = "vyral-advisory",
            ToolDenyRules = new List<string> { "shell:*" }
        };

        try
        {
            var provider = CliProviderTargets.CreateGrokBuild(new CapturingProcessRunner(), options);
            var originalConfigHash = provider.Profile.ConfigHash;
            await File.AppendAllTextAsync(command, "changed");

            var doctor = await provider.DiagnoseAsync();
            var replacement = CliProviderTargets.CreateGrokBuild(new CapturingProcessRunner(), options);

            Assert.Equal(ProviderDoctorStatuses.Failed, doctor.Status);
            Assert.Contains(doctor.Checks, check => check.Id == "executable.identity" && check.Status == ProviderDoctorStatuses.Failed);
            Assert.NotEqual(originalConfigHash, replacement.Profile.ConfigHash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AntigravityModelCatalog_ExposesGeminiModelsWithMatchingMetadata()
    {
        var provider = CliProviderTargets.CreateAntigravity(new CapturingProcessRunner());

        var catalog = await provider.ListModelsAsync();

        Assert.Equal("antigravity-cli", catalog.Provider);
        Assert.Equal(ProviderModelCatalogStatuses.Succeeded, catalog.Status);
        Assert.Equal(CliProviderTargets.DefaultGeminiModelId, catalog.DefaultModelId);
        Assert.Equal(9, catalog.Items.Count);
        var ids = catalog.Items.Select(model => model.Id).ToList();
        Assert.Contains(CliProviderTargets.DefaultGeminiModelId, ids);
        Assert.Contains(CliProviderTargets.GeminiProModelId, ids);
        Assert.Contains(CliProviderTargets.Gemma4_31BModelId, ids);
        var pro25Entry = catalog.Items.Single(model => model.Id == CliProviderTargets.GeminiProModelId);
        Assert.Equal("pro", pro25Entry.Metadata["tier"]?.ToString());
        Assert.Equal("2.5", pro25Entry.Metadata["generation"]?.ToString());
        var gemmaEntry = catalog.Items.Single(model => model.Id == CliProviderTargets.Gemma4_31BModelId);
        Assert.Equal("gemma", gemmaEntry.Metadata["family"]?.ToString());
        Assert.All(catalog.Items, model => Assert.Contains(ProviderCapabilityIds.AiChat, model.Capabilities));
    }

    [Fact]
    public async Task CliModelCatalog_ReturnsUnsupportedWhenNoModelListIsAvailable()
    {
        var provider = new CliProviderTarget(new CliProviderOptions
        {
            ProviderId = "test-cli",
            Command = "test",
            Capabilities = new List<string> { ProviderCapabilityIds.AiChat }
        }, new CapturingProcessRunner());

        var catalog = await provider.ListModelsAsync();

        Assert.Equal("test-cli", catalog.Provider);
        Assert.Equal(ProviderModelCatalogStatuses.Unsupported, catalog.Status);
        Assert.Equal(ProviderFailureClasses.Unsupported, catalog.FailureClass);
        Assert.Empty(catalog.Items);
    }

    [Fact]
    public async Task ClaudeCatalog_ReportsDefaultModelCatalog()
    {
        var provider = CliProviderTargets.CreateClaude(new CapturingProcessRunner());

        var catalog = await provider.ListModelsAsync();

        Assert.Equal("claude-cli", catalog.Provider);
        Assert.Equal(ProviderModelCatalogStatuses.Succeeded, catalog.Status);
        Assert.Equal(CliProviderTargets.DefaultClaudeModelId, catalog.DefaultModelId);
        Assert.Equal(3, catalog.Items.Count);
        Assert.Contains(CliProviderTargets.DefaultClaudeModelId, catalog.Items.Select(model => model.Id));
        Assert.Contains(CliProviderTargets.ClaudeOpus4ModelId, catalog.Items.Select(model => model.Id));
        Assert.Contains(CliProviderTargets.ClaudeHaiku4ModelId, catalog.Items.Select(model => model.Id));
        Assert.True(catalog.Items.Single(model => model.Id == CliProviderTargets.DefaultClaudeModelId).Default);
        var opusEntry = catalog.Items.Single(model => model.Id == CliProviderTargets.ClaudeOpus4ModelId);
        Assert.Equal("pro", opusEntry.Metadata["tier"]?.ToString());
        var haikuEntry = catalog.Items.Single(model => model.Id == CliProviderTargets.ClaudeHaiku4ModelId);
        Assert.Equal("lite", haikuEntry.Metadata["tier"]?.ToString());
    }

    [Fact]
    public async Task ClaudeRun_ComposesBoundaryAndWritesQuarantinedArtifacts()
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), "vyral-provider-test-" + Guid.NewGuid().ToString("N"));
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 0,
                StandardOutput = "{\"summary\":\"ok\"}"
            }
        };
        var provider = CliProviderTargets.CreateClaude(runner, new CliProviderOptions { Command = "claude-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiReview,
            Operation = "run",
            Mode = "review",
            ArtifactDirectory = artifactRoot,
            Payload = new JsonObject { ["prompt"] = "Review this retrieval change." }
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal("claude-test", runner.LastRequest?.Command);
        Assert.NotNull(runner.LastRequest);
        Assert.Contains("Vyral provider boundary", runner.LastRequest!.Arguments[1]);
        Assert.Contains("proposal or evidence", runner.LastRequest.Arguments[1]);
        Assert.Contains("Review this retrieval change.", runner.LastRequest.Arguments[1]);
        var trace = RequireTrace(result);
        Assert.NotEmpty(trace.ArtifactRefs);
        Assert.Contains(trace.ArtifactRefs, path => path.EndsWith("prompt.txt", StringComparison.Ordinal));
        Assert.Contains(trace.ArtifactRefs, path => path.EndsWith("result.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CliRun_NormalizesTimeoutFailures()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                TimedOut = true,
                ExitCode = -1,
                StandardError = "timeout"
            }
        };
        var provider = CliProviderTargets.CreateGemini(runner, new CliProviderOptions { Command = "gemini-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Payload = new JsonObject { ["prompt"] = "Answer advisory only." }
        });

        Assert.Equal(ProviderRunStatus.TimedOut, result.Status);
        Assert.Equal(ProviderFailureClasses.Timeout, result.FailureClass);
        Assert.Equal(ProviderFailureClasses.Timeout, RequireTrace(result).FailureClass);
    }

    [Fact]
    public async Task CliRun_ClassifiesStreamDisconnectsAsNetworkFailures()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 1,
                StandardError = "trust boundary retained; reconnect attempt failed: stream disconnected before completion"
            }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Payload = new JsonObject { ["prompt"] = "Answer advisory only." }
        });

        Assert.Equal(ProviderRunStatus.Failed, result.Status);
        Assert.Equal(ProviderFailureClasses.Network, result.FailureClass);
        Assert.Equal(ProviderFailureClasses.Network, RequireTrace(result).FailureClass);
        Assert.Equal("1", result.ProviderStatus);
    }

    [Fact]
    public async Task CliRun_NormalizesCancelledFailures()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                Cancelled = true,
                ExitCode = -1,
                StandardError = "cancelled"
            }
        };
        var provider = CliProviderTargets.CreateGemini(runner, new CliProviderOptions { Command = "gemini-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Payload = new JsonObject { ["prompt"] = "Answer advisory only." }
        });

        Assert.Equal(ProviderRunStatus.Cancelled, result.Status);
        Assert.Equal(ProviderFailureClasses.Cancelled, result.FailureClass);
        Assert.Equal("cancelled", result.ProviderStatus);
        Assert.Equal("cancelled", result.Output["stopReason"]?.GetValue<string>());
        Assert.Equal(true, result.Output["cancelled"]?.GetValue<bool>());
    }

    [Fact]
    public async Task CliRun_FailsClosedOnTruncatedOutputAndClampsModeLimit()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 0,
                StandardOutput = "01234567",
                OutputTruncated = true
            }
        };
        var provider = CliProviderTargets.CreateGemini(runner, new CliProviderOptions { Command = "gemini-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            MaxOutputBytes = int.MaxValue,
            Payload = new JsonObject { ["prompt"] = "Answer advisory only." }
        });

        Assert.NotNull(runner.LastRequest);
        Assert.Equal(128 * 1024, runner.LastRequest!.MaxOutputBytes);
        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Policy, result.FailureClass);
        Assert.Equal("output_limit", result.ProviderStatus);
        Assert.True(result.Output["outputTruncated"]?.GetValue<bool>());
        Assert.Equal(ProviderFailureClasses.Policy, RequireTrace(result).FailureClass);
    }

    [Fact]
    public async Task SystemRunner_KillsProcessTreeOnCancellation()
    {
        var runner = new SystemProviderProcessRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var request = CreateLongRunningProcessRequest();

        var result = await runner.RunAsync(request, cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task CliRun_AcceptsTypedChatPayload()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "Chat response." }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiChatRequest
            {
                System = "Stay advisory.",
                Messages = new List<AiMessage>
                {
                    new() { Role = "user", Content = "Summarize the retrieval context." }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Contains("System:", runner.LastRequest!.StandardInput);
        Assert.Contains("Stay advisory.", runner.LastRequest.StandardInput);
        Assert.Contains("user: Summarize the retrieval context.", runner.LastRequest.StandardInput);
        Assert.Equal("assistant", result.Output["message"]?["role"]?.GetValue<string>());
        Assert.Equal("Chat response.", result.Output["message"]?["content"]?.GetValue<string>());
        Assert.Equal("complete", result.Output["stopReason"]?.GetValue<string>());
        Assert.Equal("Chat response.", result.Output["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task CliRun_AcceptsTypedExtractPayload()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "{\"name\":\"Ada\"}" }
        };
        var provider = CliProviderTargets.CreateClaude(runner, new CliProviderOptions { Command = "claude-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiExtract,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiExtractRequest
            {
                Text = "Name: Ada",
                Instructions = "Extract the person name.",
                Schema = new JsonObject { ["name"] = "string" }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Contains("Extract structured information", runner.LastRequest!.Arguments[1]);
        Assert.Contains("Extract the person name.", runner.LastRequest.Arguments[1]);
        Assert.Contains("Name: Ada", runner.LastRequest.Arguments[1]);
        Assert.Equal("Ada", result.Output["data"]?["name"]?.GetValue<string>());
        Assert.Equal("provider_json", result.Output["validationStatus"]?.GetValue<string>());
        Assert.Equal("{\"name\":\"Ada\"}", result.Output["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task CodexRun_AcceptsStructuredExtractPayloadForListingCopy()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 0,
                StandardOutput = """
                    {
                      "targetAttributePath": "/attributes/bullet_point",
                      "draftCopy": "Lightweight blackout curtains for nursery windows.",
                      "draftBullets": ["Blocks light", "Easy to hang"],
                      "description": "Soft blackout panels for everyday nursery use.",
                      "backendTerms": ["blackout curtain", "nursery drape"],
                      "reviewNotes": ["Check exact dimensions before publish."],
                      "riskNotes": ["Avoid quantified light-blocking claims without source support."],
                      "claimsNeedingReview": ["Blocks 100% of light"],
                      "evidenceRefs": ["manual:install:page-2"]
                    }
                    """
            }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiExtract,
            Operation = "run",
            ModelId = CliProviderTargets.DefaultCodexModelId,
            Payload = ProviderJson.ToJsonObject(new AiExtractRequest
            {
                Text = "SKU RWIG9998-FBA0. Product: nursery blackout curtains. Evidence: install manual page 2.",
                Instructions = "Draft product listing copy fields for review. Flag unsupported claims.",
                Schema = new JsonObject
                {
                    ["targetAttributePath"] = "/attributes/bullet_point",
                    ["draftCopy"] = "string",
                    ["draftBullets"] = new JsonArray(JsonValue.Create("string")),
                    ["description"] = "string",
                    ["backendTerms"] = new JsonArray(JsonValue.Create("string")),
                    ["reviewNotes"] = new JsonArray(JsonValue.Create("string")),
                    ["riskNotes"] = new JsonArray(JsonValue.Create("string")),
                    ["claimsNeedingReview"] = new JsonArray(JsonValue.Create("string")),
                    ["evidenceRefs"] = new JsonArray(JsonValue.Create("string"))
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal(CliProviderTargets.DefaultCodexModelId, runner.LastRequest!.Arguments[2]);
        Assert.Contains("Return a JSON object only.", runner.LastRequest.StandardInput);
        Assert.Contains("Do not execute, plan, or describe tool use", runner.LastRequest.StandardInput);
        Assert.Contains("Use only the supplied payload.", runner.LastRequest.StandardInput);
        Assert.Contains("Draft product listing copy fields for review.", runner.LastRequest.StandardInput);
        Assert.Equal(CliProviderTargets.DefaultCodexModelId, RequireTrace(result).ModelId);
        Assert.Equal("/attributes/bullet_point", result.Output["data"]?["targetAttributePath"]?.GetValue<string>());
        Assert.Equal("Lightweight blackout curtains for nursery windows.", result.Output["data"]?["draftCopy"]?.GetValue<string>());
        Assert.Equal("Blocks light", result.Output["data"]?["draftBullets"]?[0]?.GetValue<string>());
        Assert.Equal("blackout curtain", result.Output["data"]?["backendTerms"]?[0]?.GetValue<string>());
        Assert.Equal("manual:install:page-2", result.Output["data"]?["evidenceRefs"]?[0]?.GetValue<string>());
        Assert.Equal("provider_json", result.Output["validationStatus"]?.GetValue<string>());
        Assert.Null(result.FailureClass);
    }

    [Fact]
    public async Task CliRun_RejectsExtractSuccessWithoutStructuredJson()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "Here is some copy, but not JSON." }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiExtract,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiExtractRequest
            {
                Text = "Product source material.",
                Schema = new JsonObject { ["draftCopy"] = "string" }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Schema, result.FailureClass);
        Assert.Equal("invalid_provider_json", result.ProviderStatus);
        Assert.NotNull(result.Rejection);
        Assert.Equal(ProviderRejectionSources.VyralClassification, result.Rejection!.Source);
        Assert.Equal(ProviderRejectionDecisionAuthorities.VyralStructuredOutputValidation, result.Rejection.DecisionAuthority);
        Assert.Equal(ProviderProcessOutcomes.ExitZero, result.Rejection.ProcessOutcome);
        Assert.False(result.Rejection.ParsedOutputPresent);
        Assert.False(result.Rejection.StructuredOutputAccepted);
        Assert.Equal("not_validated", result.Output["validationStatus"]?.GetValue<string>());
        Assert.Equal("Here is some copy, but not JSON.", result.Output["data"]?["rawText"]?.GetValue<string>());
    }

    [Fact]
    public async Task CliRun_RejectsPolicyFailureWithParsedOutputAndQuarantineGuidance()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 1,
                StandardError = "content policy rejected request",
                StandardOutput = "{\"draftCopy\":\"Do not use this copy even though it parsed.\"}"
            }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiExtract,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiExtractRequest
            {
                Text = "Product source material.",
                Schema = new JsonObject { ["draftCopy"] = "string" }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Policy, result.FailureClass);
        Assert.Equal("1", result.ProviderStatus);
        Assert.NotNull(result.Rejection);
        Assert.Equal(ProviderRejectionSources.ProviderPolicy, result.Rejection!.Source);
        Assert.Equal("provider_policy", result.Rejection.Category);
        Assert.False(result.Rejection.ContentUsable);
        Assert.True(result.Rejection.ParsedOutputPresent);
        Assert.True(result.Rejection.StructuredOutputAccepted);
        Assert.Equal("provider_json", result.Rejection.StructuredOutputValidationStatus);
        Assert.Equal(ProviderRejectionDecisionAuthorities.ProviderProcessExit, result.Rejection.DecisionAuthority);
        Assert.Equal(ProviderProcessOutcomes.ExitNonZero, result.Rejection.ProcessOutcome);
        Assert.Equal(ProviderParsedOutputDispositions.QuarantineForOperatorReview, result.Rejection.ParsedOutputDisposition);
        Assert.Equal(ProviderRetryRecommendations.RetryWithRedactedInput, result.Rejection.RetryRecommendation);
        Assert.True(result.Rejection.OperatorReviewRecommended);
        Assert.Equal("Do not use this copy even though it parsed.", result.Output["data"]?["draftCopy"]?.GetValue<string>());
        Assert.Equal("quarantine_for_operator_review", result.Output["rejection"]?["parsedOutputDisposition"]?.GetValue<string>());
        Assert.Equal("provider_process_exit", result.Output["rejection"]?["decisionAuthority"]?.GetValue<string>());
        Assert.True(result.Output["rejection"]?["structuredOutputAccepted"]?.GetValue<bool>());
    }

    [Fact]
    public async Task CliRun_RejectsExtractToolPlanShapedJsonAsBoundaryLeakage()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 0,
                StandardOutput = "{\"calls\":[{\"tool\":\"shell\",\"arguments\":{\"cmd\":\"ls\"}}]}"
            }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiExtract,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiExtractRequest
            {
                Text = "Product source material.",
                Schema = new JsonObject { ["draftCopy"] = "string" }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Trust, result.FailureClass);
        Assert.Equal("tool_plan_leakage", result.ProviderStatus);
        Assert.Contains("ai.toolPlan", result.Error ?? string.Empty);
        Assert.Equal("rejected_tool_plan_leakage", result.Output["validationStatus"]?.GetValue<string>());
        Assert.Equal("tool_plan_leakage", result.Output["boundaryViolation"]?.GetValue<string>());
        Assert.NotNull(result.Output["data"]?["calls"]);
    }

    [Fact]
    public async Task CliRun_RejectsExtractWorkspaceExplorationTextAsBoundaryLeakage()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 0,
                StandardOutput = "I will list directories, query SQLite, and read provider-run request files before drafting copy."
            }
        };
        var provider = CliProviderTargets.CreateAntigravity(runner, new CliProviderOptions { Command = "agy-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiExtract,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiExtractRequest
            {
                Text = "Product source material.",
                Schema = new JsonObject { ["draftCopy"] = "string" }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Trust, result.FailureClass);
        Assert.Equal("tool_plan_leakage", result.ProviderStatus);
        Assert.Contains("workspace/tool exploration", result.Error ?? string.Empty);
        Assert.Equal("rejected_tool_plan_leakage", result.Output["validationStatus"]?.GetValue<string>());
        Assert.Equal("tool_plan_leakage", result.Output["boundaryViolation"]?.GetValue<string>());
        Assert.Equal("I will list directories, query SQLite, and read provider-run request files before drafting copy.", result.Output["data"]?["rawText"]?.GetValue<string>());
    }

    [Fact]
    public async Task CliRun_AcceptsTypedRerankPayloadAndNormalizesOutput()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 0,
                StandardOutput = "{\"items\":[{\"id\":\"retention\",\"rank\":1,\"score\":0.95},{\"id\":\"travel\",\"rank\":2,\"score\":0.15}]}"
            }
        };
        var provider = CliProviderTargets.CreateGemini(runner, new CliProviderOptions { Command = "gemini-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiRerank,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiRerankRequest
            {
                Query = "retention policy",
                Limit = 2,
                Candidates = new List<AiRerankCandidate>
                {
                    new() { Id = "travel", Text = "travel reimbursement policy" },
                    new() { Id = "retention", Text = "active retention policy details" }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Contains("Rerank the candidates", runner.LastRequest!.Arguments.Last());
        Assert.Contains("retention policy", runner.LastRequest.Arguments.Last());
        Assert.Equal("provider_json", result.Output["validationStatus"]?.GetValue<string>());
        var first = result.Output["items"]!.AsArray()[0]!;
        Assert.Equal("retention", first["id"]!.GetValue<string>());
        Assert.Equal(1, first["rank"]!.GetValue<int>());
    }

    [Fact]
    public async Task CliRun_RejectsRerankSuccessWithoutStructuredJson()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "retention should be first" }
        };
        var provider = CliProviderTargets.CreateGemini(runner, new CliProviderOptions { Command = "gemini-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiRerank,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiRerankRequest
            {
                Query = "retention policy",
                Candidates = new List<AiRerankCandidate>
                {
                    new() { Id = "retention", Text = "active retention policy details" }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Schema, result.FailureClass);
        Assert.Equal("invalid_provider_json", result.ProviderStatus);
        Assert.Empty(result.Output["items"]!.AsArray());
        Assert.Equal("not_validated", result.Output["validationStatus"]?.GetValue<string>());
    }

    [Fact]
    public async Task CliRun_AcceptsTypedReviewPayloadAndNormalizesOutput()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 0,
                StandardOutput = "{\"summary\":\"needs tests\",\"findings\":[{\"id\":\"f1\",\"severity\":\"warning\",\"message\":\"Add coverage\"}]}"
            }
        };
        var provider = CliProviderTargets.CreateGemini(runner, new CliProviderOptions { Command = "gemini-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiReview,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiReviewRequest
            {
                Prompt = "Review provider readiness.",
                Subject = "Provider adapter diff",
                References = new List<AiReference>
                {
                    new() { Id = "diff:1", Kind = "diff", ContentHash = "sha256:abc" }
                },
                MaxFindings = 3
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Contains("Expected JSON fields", runner.LastRequest!.Arguments.Last());
        Assert.Contains("Provider adapter diff", runner.LastRequest.Arguments.Last());
        Assert.Contains("diff:1", runner.LastRequest.Arguments.Last());
        Assert.Equal("needs tests", result.Output["summary"]?.GetValue<string>());
        Assert.Equal("provider_json", result.Output["validationStatus"]?.GetValue<string>());
        Assert.NotEmpty(result.Output["findings"]!.AsArray());
        Assert.NotNull(result.Output["references"]);
    }

    [Fact]
    public async Task CliRun_AcceptsTypedScaffoldPayloadAndWrapsRawOutput()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "Create a provider adapter shell." }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiScaffold,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiScaffoldRequest
            {
                Prompt = "Scaffold provider adapter.",
                AllowedPaths = new List<string> { "adapters/vyral_provider.py" },
                MaxArtifacts = 2
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Contains("Propose scaffold artifacts", runner.LastRequest!.StandardInput);
        Assert.Contains("adapters/vyral_provider.py", runner.LastRequest.StandardInput);
        Assert.Equal("Create a provider adapter shell.", result.Output["summary"]?.GetValue<string>());
        Assert.Equal("not_validated", result.Output["validationStatus"]?.GetValue<string>());
        Assert.Empty(result.Output["artifacts"]!.AsArray());
        Assert.Equal("Create a provider adapter shell.", result.Output["text"]?.GetValue<string>());
        Assert.Equal("Create a provider adapter shell.", result.Output["rawText"]?.GetValue<string>());
    }

    [Fact]
    public async Task CliRun_NormalizesFencedScaffoldJson()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 0,
                StandardOutput = """
                    Here is the scaffold:
                    ```json
                    {"content":"Create adapter shell.","artifacts":[{"path":"adapters/vyral_provider.py","action":"propose","content":"pass"}]}
                    ```
                    """
            }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiScaffold,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiScaffoldRequest
            {
                Prompt = "Scaffold provider adapter.",
                AllowedPaths = new List<string> { "adapters/vyral_provider.py" }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.Equal("Create adapter shell.", result.Output["summary"]?.GetValue<string>());
        Assert.Equal("provider_jsonish", result.Output["validationStatus"]?.GetValue<string>());
        var artifact = Assert.Single(result.Output["artifacts"]!.AsArray());
        Assert.Equal("adapters/vyral_provider.py", artifact!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task CliRun_AcceptsTypedToolPlanPayloadAndNormalizesOutput()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult
            {
                ExitCode = 0,
                StandardOutput = "{\"calls\":[{\"tool\":\"search\",\"arguments\":{\"query\":\"retention\"},\"requiresApproval\":true,\"rationale\":\"Need local evidence.\"}]}"
            }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiToolPlan,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiToolPlanRequest
            {
                Prompt = "Should I call search before answering?",
                Tools = new List<AiToolDefinition>
                {
                    new()
                    {
                        Name = "search",
                        Description = "Search local records.",
                        InputSchema = new JsonObject { ["type"] = "object" }
                    }
                }
            })
        });

        Assert.Equal(ProviderRunStatus.Succeeded, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Contains("Plan tool calls", runner.LastRequest!.StandardInput);
        Assert.Contains("Return only structured tool-call proposals", runner.LastRequest.StandardInput);
        Assert.Contains("Do not execute tools, inspect a workspace", runner.LastRequest.StandardInput);
        Assert.Contains("Search local records.", runner.LastRequest.StandardInput);
        Assert.Equal("provider_json", result.Output["validationStatus"]?.GetValue<string>());
        var call = Assert.Single(result.Output["calls"]!.AsArray());
        Assert.Equal("search", call!["tool"]!.GetValue<string>());
        Assert.True(call["requiresApproval"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CliRun_RejectsToolPlanSuccessWithoutStructuredJson()
    {
        var runner = new CapturingProcessRunner
        {
            Result = new ProviderProcessRunResult { ExitCode = 0, StandardOutput = "I would call search." }
        };
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiToolPlan,
            Operation = "run",
            Payload = ProviderJson.ToJsonObject(new AiToolPlanRequest
            {
                Prompt = "Should I call search?",
                Tools = new List<AiToolDefinition> { new() { Name = "search" } }
            })
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Schema, result.FailureClass);
        Assert.Equal("invalid_provider_json", result.ProviderStatus);
        Assert.Empty(result.Output["calls"]!.AsArray());
        Assert.Equal("not_validated", result.Output["validationStatus"]?.GetValue<string>());
    }

    [Fact]
    public async Task CliRun_RejectsUnknownModeBeforeProcessExecution()
    {
        var runner = new CapturingProcessRunner();
        var provider = CliProviderTargets.CreateCodex(runner, new CliProviderOptions { Command = "codex-test" });

        var result = await provider.RunAsync(new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Mode = "unknown-mode",
            Payload = new JsonObject { ["prompt"] = "Do not run this." }
        });

        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal(ProviderFailureClasses.Policy, result.FailureClass);
        Assert.Equal("unknown_mode", result.ProviderStatus);
        Assert.Null(runner.LastRequest);
    }

    private sealed class CapturingProcessRunner : IProviderProcessRunner
    {
        public ProviderProcessRunRequest? LastRequest { get; private set; }
        public ProviderProcessRunResult Result { get; set; } = new() { ExitCode = 0, StandardOutput = "ok" };
        public Action<ProviderProcessRunRequest>? OnRun { get; set; }

        public Task<ProviderProcessRunResult> RunAsync(ProviderProcessRunRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            OnRun?.Invoke(request);
            return Task.FromResult(Result);
        }
    }

    private static ProviderTraceEvent RequireTrace(ProviderRunResult result) =>
        Assert.IsType<ProviderTraceEvent>(result.Trace);

    private sealed class CapturingCodexAppServerQuotaClient : ICodexAppServerQuotaClient
    {
        public CodexAppServerQuotaRequest? LastRequest { get; private set; }
        public ProviderProcessRunResult Result { get; set; } = new() { ExitCode = 0 };

        public Task<ProviderProcessRunResult> ReadRateLimitsAsync(CodexAppServerQuotaRequest request, CancellationToken ct = default)
        {
            _ = ct;
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private static ProviderProcessRunRequest CreateLongRunningProcessRequest()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProviderProcessRunRequest
            {
                Command = "cmd",
                Arguments = new[] { "/c", "ping -n 30 127.0.0.1 > nul" },
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        return new ProviderProcessRunRequest
        {
            Command = "/bin/sh",
            Arguments = new[] { "-c", "sleep 30" },
            Timeout = TimeSpan.FromSeconds(30)
        };
    }
}
