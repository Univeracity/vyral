using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Vyral.Execution;
using Vyral.Local;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Local;
using Vyral.Server;

namespace Vyral.Tests.Local;

public class ServerErrorHandlingTests
{
    [Fact]
    public async Task RecordDelete_RemovesRecordAndIsIdempotent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var collection = await client.PostAsJsonAsync("/collections", new
        {
            name = "records",
            partitionKeyPath = "/partitionKey"
        });
        collection.EnsureSuccessStatusCode();
        var createRun = (await collection.Content.ReadFromJsonAsync<ExecutionRun>())!;
        await WaitForExecutionRunAsync(client, createRun.Id, ExecutionRunStatuses.Succeeded);

        var upsert = await client.PostAsJsonAsync("/collections/records/records", new
        {
            id = "record-1",
            partitionKey = "tenant-a",
            content = new { text = "delete me" }
        });
        upsert.EnsureSuccessStatusCode();

        var firstDelete = await client.DeleteAsync("/collections/records/records/tenant-a/record-1");
        var secondDelete = await client.DeleteAsync("/collections/records/records/tenant-a/record-1");
        var get = await client.GetAsync("/collections/records/records/tenant-a/record-1");

        Assert.Equal(HttpStatusCode.NoContent, firstDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task CollectionDelete_RemovesCollectionAndIsIdempotent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var collection = await client.PostAsJsonAsync("/collections", new
        {
            name = "records",
            partitionKeyPath = "/partitionKey"
        });
        collection.EnsureSuccessStatusCode();
        var createRun = (await collection.Content.ReadFromJsonAsync<ExecutionRun>())!;
        await WaitForExecutionRunAsync(client, createRun.Id, ExecutionRunStatuses.Succeeded);

        var upsert = await client.PostAsJsonAsync("/collections/records/records", new
        {
            id = "record-1",
            partitionKey = "tenant-a",
            content = new { text = "delete with collection" }
        });
        upsert.EnsureSuccessStatusCode();

        using var firstDeleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/collections/records");
        firstDeleteRequest.Headers.Add("Idempotency-Key", "delete-records");
        var firstDelete = await client.SendAsync(firstDeleteRequest);
        using var secondDeleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/collections/records");
        secondDeleteRequest.Headers.Add("Idempotency-Key", "delete-records");
        var secondDelete = await client.SendAsync(secondDeleteRequest);
        var firstDeleteRun = (await firstDelete.Content.ReadFromJsonAsync<ExecutionRun>())!;
        var secondDeleteRun = (await secondDelete.Content.ReadFromJsonAsync<ExecutionRun>())!;
        await WaitForExecutionRunAsync(client, firstDeleteRun.Id, ExecutionRunStatuses.Succeeded);
        var getPolicy = await client.GetAsync("/collections/records");
        var getRecord = await client.GetAsync("/collections/records/records/tenant-a/record-1");
        var collections = await client.GetFromJsonAsync<List<string>>("/collections");

        Assert.Equal(HttpStatusCode.Accepted, firstDelete.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, secondDelete.StatusCode);
        Assert.Equal(firstDeleteRun.Id, secondDeleteRun.Id);
        Assert.True(secondDeleteRun.Admission.Replayed);
        Assert.Equal(HttpStatusCode.NotFound, getPolicy.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, getRecord.StatusCode);
        Assert.NotNull(collections);
        Assert.DoesNotContain("records", collections);
    }

    [Fact]
    public async Task ObjectPreconditionFailure_ReturnsPreconditionFailedProblem()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();
        var first = await client.PutAsync("/objects/objects/docs/a.txt", new StringContent("one", Encoding.UTF8, "text/plain"));
        first.EnsureSuccessStatusCode();

        using var duplicate = new HttpRequestMessage(HttpMethod.Put, "/objects/objects/docs/a.txt")
        {
            Content = new StringContent("two", Encoding.UTF8, "text/plain")
        };
        duplicate.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);

        var response = await client.SendAsync(duplicate);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Contains("precondition failed", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetrievalConfigurationFailure_ReturnsStructuredNotFoundProblem()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/search", new
        {
            query = "retention",
            collections = new[] { "missing" },
            limit = 1
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("System.InvalidOperationException", body);
        Assert.Contains("Collection 'missing' does not exist.", body);
    }

    [Fact]
    public async Task CollectionSearchMissingCollection_ReturnsStructuredNotFoundProblem()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/collections/missing/search", new
        {
            vector = new
            {
                field = "contentEmbedding",
                value = new[] { 1.0f, 0.0f },
                top = 1
            },
            limit = 1
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("System.InvalidOperationException", body);
        Assert.Contains("Collection 'missing' does not exist.", body);
    }

    [Fact]
    public async Task EmbeddingValidationFailure_ReturnsBadRequestProblem()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/embeddings", new
        {
            texts = new[] { "" }
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Embedding request text values cannot be empty.", body);
    }

    [Fact]
    public async Task BatchUpsertValidationFailure_ReturnsBadRequestProblem()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/collections/records/records/batch", new
        {
            records = Array.Empty<object>()
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Batch upsert request must include at least one record.", body);
    }

    [Fact]
    public async Task ProductionServer_RedactsUnexpectedAdapterRejectionDetails()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-production-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-production-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(
            dbPath,
            objectsPath,
            new Dictionary<string, string?> { ["CanonicalStore:Enabled"] = "false" },
            Environments.Production);
        var client = factory.CreateClient();
        var collection = await client.PostAsJsonAsync("/collections", new
        {
            name = "records",
            partitionKeyPath = "/partitionKey"
        });
        collection.EnsureSuccessStatusCode();
        var createRun = (await collection.Content.ReadFromJsonAsync<ExecutionRun>())!;
        await WaitForExecutionRunAsync(client, createRun.Id, ExecutionRunStatuses.Succeeded);

        var response = await client.PostAsJsonAsync("/collections/records/query", new
        {
            filter = new
            {
                path = "/id",
                op = "private-sensitive-operator",
                value = "record-1"
            }
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Request rejected", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-sensitive-operator", body, StringComparison.Ordinal);
        Assert.DoesNotContain("NotSupportedException", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RagIngestionConfigurationFailure_CompletesDurableAdmissionAsFailed()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var collection = await client.PostAsJsonAsync("/collections", new
        {
            name = "chunks",
            vectorPolicies = new[]
            {
                new
                {
                    name = "contentEmbedding",
                    path = "/vectors/contentEmbedding/values",
                    dimensions = 2
                }
            }
        });
        collection.EnsureSuccessStatusCode();
        var createRun = (await collection.Content.ReadFromJsonAsync<ExecutionRun>())!;
        await WaitForExecutionRunAsync(client, createRun.Id, ExecutionRunStatuses.Succeeded);

        var response = await client.PostAsJsonAsync("/collections/chunks/rag/ingest-text", new
        {
            partitionKey = "tenant-a",
            text = "retention policy"
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var admitted = (await response.Content.ReadFromJsonAsync<RagIngestionJob>())!;
        var completed = await WaitForRagIngestionJobAsync(client, admitted.Id);

        Assert.Equal(RagIngestionJobStatuses.Failed, completed.Status);
        Assert.Contains("Embedding provider returns 384 dimensions", completed.Error);
    }

    [Fact]
    public async Task Cors_AllowsConfiguredLocalDevelopmentOrigin()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/health");
        preflight.Headers.Add("Origin", "http://localhost:5173");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("GET", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
    }

    [Fact]
    public async Task ApiKeyConfiguration_ProtectsPrivateRoutesAndAllowsHealth()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, new Dictionary<string, string?>
        {
            ["VYRAL_API_KEY"] = "test-secret",
            ["VYRAL_API_KEY_HEADER"] = "X-Test-Vyral-Key"
        });
        var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        var unauthorized = await client.GetAsync("/collections");
        using var authorizedRequest = new HttpRequestMessage(HttpMethod.Get, "/collections");
        authorizedRequest.Headers.Add("X-Test-Vyral-Key", "test-secret");
        var authorized = await client.SendAsync(authorizedRequest);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
    }

    [Fact]
    public async Task ProviderRunGuard_RejectsLongJobsAndRateLimitedRuns()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath, new Dictionary<string, string?>
        {
            ["Providers:DefaultRunTimeoutSeconds"] = "1",
            ["Providers:MaxRunTimeoutSeconds"] = "2",
            ["Providers:MaxRunsPerWindow"] = "1",
            ["Providers:RateLimitWindowSeconds"] = "60"
        });
        var client = factory.CreateClient();

        var tooLong = await client.PostAsJsonAsync($"/providers/{DeterministicAiProviderTarget.ProviderId}/run", new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            TimeoutSeconds = 99,
            Payload = ProviderJson.ToJsonObject(new AiChatRequest
            {
                Messages = new List<AiMessage> { new() { Role = "user", Content = "hello" } }
            })
        });
        tooLong.EnsureSuccessStatusCode();
        var tooLongAdmission = (await tooLong.Content.ReadFromJsonAsync<ProviderRunJob>())!;
        var tooLongResult = (await WaitForProviderRunJobAsync(client, tooLongAdmission.Id)).Result;

        var first = await client.PostAsJsonAsync($"/providers/{DeterministicAiProviderTarget.ProviderId}/run", new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Payload = ProviderJson.ToJsonObject(new AiChatRequest
            {
                Messages = new List<AiMessage> { new() { Role = "user", Content = "first" } }
            })
        });
        first.EnsureSuccessStatusCode();
        var firstAdmission = (await first.Content.ReadFromJsonAsync<ProviderRunJob>())!;
        var firstResult = (await WaitForProviderRunJobAsync(client, firstAdmission.Id)).Result;

        var second = await client.PostAsJsonAsync($"/providers/{DeterministicAiProviderTarget.ProviderId}/run", new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Payload = ProviderJson.ToJsonObject(new AiChatRequest
            {
                Messages = new List<AiMessage> { new() { Role = "user", Content = "second" } }
            })
        });
        second.EnsureSuccessStatusCode();
        var secondAdmission = (await second.Content.ReadFromJsonAsync<ProviderRunJob>())!;
        var secondResult = (await WaitForProviderRunJobAsync(client, secondAdmission.Id)).Result;

        Assert.NotNull(tooLongResult);
        Assert.Equal(ProviderRunStatus.Rejected, tooLongResult.Status);
        Assert.Equal(ProviderFailureClasses.Policy, tooLongResult.FailureClass);
        Assert.Equal("timeout_exceeds_policy", tooLongResult.ProviderStatus);
        Assert.Equal(ProviderRunStatus.Succeeded, firstResult!.Status);
        Assert.NotNull(secondResult);
        Assert.Equal(ProviderRunStatus.Rejected, secondResult.Status);
        Assert.Equal(ProviderFailureClasses.RateLimit, secondResult.FailureClass);
        Assert.Equal("rate_limited", secondResult.ProviderStatus);
    }

    [Fact]
    public async Task ProviderRun_RejectsInvalidMeteringChainContextBeforeAdmission()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/providers/{DeterministicAiProviderTarget.ProviderId}/run", new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Payload = ProviderJson.ToJsonObject(new AiChatRequest
            {
                Messages = new List<AiMessage> { new() { Role = "user", Content = "hello" } }
            }),
            MeteringContext = new AiMeteringContext
            {
                RunnerSessionId = "runner-session-1",
                Sequence = 2
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("previousReceiptHash", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderRunJobStore_CancelsRunningJobs()
    {
        var jobs = new ProviderRunJobStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Mode = "advisory"
        };

        var job = jobs.Start("test-provider", request, async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new ProviderRunResult
            {
                Status = ProviderRunStatus.Succeeded,
                Provider = "test-provider",
                Capability = request.Capability,
                Operation = request.Operation,
                Mode = request.Mode
            };
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancelling = jobs.Cancel(job.Id);
        Assert.NotNull(cancelling);
        Assert.True(cancelling!.CancellationRequested);

        ProviderRunJob? completed = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            completed = jobs.Get(job.Id);
            if (completed is not null && completed.Status == ProviderJobStatus.Cancelled)
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.NotNull(completed);
        Assert.Equal(ProviderJobStatus.Cancelled, completed!.Status);
        Assert.NotNull(completed.Result);
        Assert.Equal(ProviderRunStatus.Cancelled, completed.Result.Status);
        Assert.Equal(ProviderFailureClasses.Cancelled, completed.Result.FailureClass);
        Assert.Equal("cancelled", completed.ProviderStatus);
        Assert.NotNull(completed.TraceId);
    }

    [Fact]
    public async Task ProviderRunJobStore_BoundsActiveAndRetainedJobs()
    {
        var jobs = new ProviderRunJobStore(new ProviderRunJobStoreOptions
        {
            MaxActiveJobs = 1,
            MaxRetainedTerminalJobs = 2,
            DefaultListLimit = 1,
            MaxListLimit = 2
        });
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Mode = "advisory"
        };

        var first = jobs.Start("test-provider", request, async (_, ct) =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task.WaitAsync(ct);
            return SuccessfulResult("test-provider", request);
        });

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rejected = jobs.Start("test-provider", new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Mode = "advisory"
        }, (_, _) => Task.FromResult(SuccessfulResult("test-provider", request)));

        Assert.Equal(ProviderJobStatus.Rejected, rejected.Status);
        Assert.Equal("job_queue_full", rejected.ProviderStatus);

        releaseFirst.SetResult();
        await WaitForJobStatusAsync(jobs, first.Id, ProviderJobStatus.Succeeded);

        var third = jobs.Start("test-provider", new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Mode = "advisory"
        }, (_, _) => Task.FromResult(SuccessfulResult("test-provider", request)));
        await WaitForJobStatusAsync(jobs, third.Id, ProviderJobStatus.Succeeded);

        var fourth = jobs.Start("test-provider", new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Mode = "advisory"
        }, (_, _) => Task.FromResult(SuccessfulResult("test-provider", request)));
        await WaitForJobStatusAsync(jobs, fourth.Id, ProviderJobStatus.Succeeded);

        Assert.Single(jobs.List());
        Assert.Equal(2, jobs.List(limit: 2).Count);
        Assert.Null(jobs.Get(rejected.Id));
        Assert.Null(jobs.Get(first.Id));
        Assert.Throws<InvalidOperationException>(() => jobs.List(limit: 3));
    }

    [Fact]
    public async Task ProviderRunJobStore_PersistsTerminalJobsAcrossInstances()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-jobs-{Guid.NewGuid():N}.sqlite");
        await InitializeJobDatabaseAsync(dbPath);
        var persistence = new SqliteProviderRunJobPersistence(dbPath);
        var options = new ProviderRunJobStoreOptions
        {
            MaxRetainedTerminalJobs = 10,
            DefaultListLimit = 10,
            MaxListLimit = 10
        };
        var request = new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Mode = "advisory"
        };

        var jobs = new ProviderRunJobStore(options, persistence);
        var job = jobs.Start("test-provider", request, (_, _) => Task.FromResult(SuccessfulResult("test-provider", request)));
        await WaitForJobStatusAsync(jobs, job.Id, ProviderJobStatus.Succeeded);

        var reloaded = new ProviderRunJobStore(options, persistence);
        var loaded = reloaded.Get(job.Id);
        Assert.NotNull(loaded);
        Assert.Equal(ProviderJobStatus.Succeeded, loaded!.Status);
        Assert.NotNull(loaded.Result);
        Assert.Equal(ProviderRunStatus.Succeeded, loaded.Result.Status);

        var summaries = reloaded.List(provider: "test-provider");
        Assert.Contains(summaries, item => item.Id == job.Id);
        Assert.All(summaries, item => Assert.Null(item.Result));

        var full = reloaded.List(provider: "test-provider", includeResult: true);
        Assert.Contains(full, item => item.Id == job.Id && item.Result is not null);

        var pruningOptions = new ProviderRunJobStoreOptions
        {
            MaxRetainedTerminalJobs = 1,
            DefaultListLimit = 10,
            MaxListLimit = 10
        };
        var pruning = new ProviderRunJobStore(pruningOptions, persistence);
        var second = pruning.Start("test-provider", new ProviderRunRequest
        {
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Mode = "advisory"
        }, (_, _) => Task.FromResult(SuccessfulResult("test-provider", request)));
        await WaitForJobStatusAsync(pruning, second.Id, ProviderJobStatus.Succeeded);

        var prunedReload = new ProviderRunJobStore(pruningOptions, persistence);
        Assert.Single(prunedReload.List(provider: "test-provider", limit: 10, includeResult: true));
        Assert.Null(prunedReload.Get(job.Id));
        Assert.NotNull(prunedReload.Get(second.Id));
    }

    [Fact]
    public async Task ProviderRunJobStore_MarksPersistedActiveJobsInterruptedOnStartup()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-jobs-interrupted-{Guid.NewGuid():N}.sqlite");
        await InitializeJobDatabaseAsync(dbPath);
        var persistence = new SqliteProviderRunJobPersistence(dbPath);
        var active = new ProviderRunJob
        {
            Id = "job-active",
            Status = ProviderJobStatus.Running,
            Provider = "test-provider",
            Capability = ProviderCapabilityIds.AiChat,
            Operation = "run",
            Mode = "advisory",
            CorrelationId = "correlation-1",
            RequestHash = "sha256:active",
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            StartedAt = DateTime.UtcNow.AddMinutes(-1)
        };
        persistence.Upsert(active);

        var jobs = new ProviderRunJobStore(new ProviderRunJobStoreOptions(), persistence);

        var interrupted = jobs.Get(active.Id);
        Assert.NotNull(interrupted);
        Assert.Equal(ProviderJobStatus.Failed, interrupted!.Status);
        Assert.Equal("job_interrupted", interrupted.ProviderStatus);
        Assert.Equal(ProviderFailureClasses.Unknown, interrupted.FailureClass);
        Assert.NotNull(interrupted.Result);
        Assert.Equal(ProviderRunStatus.Failed, interrupted.Result.Status);

        var reloaded = new ProviderRunJobStore(new ProviderRunJobStoreOptions(), persistence);
        Assert.Equal(ProviderJobStatus.Failed, reloaded.Get(active.Id)?.Status);
    }

    private static async Task WaitForJobStatusAsync(ProviderRunJobStore jobs, string jobId, ProviderJobStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        ProviderRunJob? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = jobs.Get(jobId);
            if (last?.Status == status)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Job {jobId} did not reach status {status}. Last observed status: {last?.Status.ToString() ?? "<missing>"}.");
    }

    private static ProviderRunResult SuccessfulResult(string provider, ProviderRunRequest request)
    {
        return new ProviderRunResult
        {
            Status = ProviderRunStatus.Succeeded,
            Provider = provider,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            Trace = new ProviderTraceEvent { TraceId = Guid.NewGuid().ToString("N") }
        };
    }

    [Fact]
    public async Task UnknownProvider_RunReturnsStructuredProblem()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/providers/gemini-cli/run", new
        {
            capability = "ai.extract",
            payload = new { }
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("gemini-cli", body);
        Assert.Contains("EnableLiveTargets", body);
    }

    [Fact]
    public async Task UnknownProvider_JobsReturnsStructuredProblem()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-server-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-server-objects-{Guid.NewGuid():N}");

        await using var factory = CreateFactory(dbPath, objectsPath);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/providers/gemini-cli/jobs", new
        {
            capability = "ai.extract",
            payload = new { }
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("gemini-cli", body);
        Assert.Contains("EnableLiveTargets", body);
    }

    private static async Task InitializeJobDatabaseAsync(string dbPath)
    {
        var traces = new SqliteTraceStore(dbPath);
        await traces.InitializeAsync();
    }

    private static async Task<ExecutionRun> WaitForExecutionRunAsync(
        HttpClient client,
        string runId,
        string expectedStatus)
    {
        ExecutionRun? run = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            run = await client.GetFromJsonAsync<ExecutionRun>($"/execution/runs/{runId}");
            if (run?.Status == expectedStatus)
            {
                return run;
            }
            if (run is not null && ExecutionRunStatuses.IsTerminal(run.Status))
            {
                break;
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException(
            $"Execution run {runId} reached {run?.Status ?? "missing"}, expected {expectedStatus}.");
    }

    private static async Task<RagIngestionJob> WaitForRagIngestionJobAsync(HttpClient client, string jobId)
    {
        RagIngestionJob? job = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            job = await client.GetFromJsonAsync<RagIngestionJob>($"/rag/ingestion/jobs/{jobId}");
            if (job is not null && job.Status is not (RagIngestionJobStatuses.Queued or RagIngestionJobStatuses.Running))
            {
                return job;
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException($"RAG ingestion job {jobId} did not reach a terminal state.");
    }

    private static async Task<ProviderRunJob> WaitForProviderRunJobAsync(HttpClient client, string jobId)
    {
        ProviderRunJob? job = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            job = await client.GetFromJsonAsync<ProviderRunJob>($"/provider-jobs/{jobId}");
            if (job is not null && job.Status is not (ProviderJobStatus.Queued or ProviderJobStatus.Running))
            {
                return job;
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException($"Provider run job {jobId} did not reach a terminal state.");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string dbPath,
        string objectsPath,
        Dictionary<string, string?>? extraConfiguration = null,
        string? environment = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                if (!string.IsNullOrWhiteSpace(environment))
                {
                    builder.UseEnvironment(environment);
                }
                builder.UseSetting("DatabasePath", dbPath);
                builder.UseSetting("ObjectsPath", objectsPath);
                foreach (var (key, value) in extraConfiguration ?? new Dictionary<string, string?>())
                {
                    builder.UseSetting(key, value);
                }

                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    var values = new Dictionary<string, string?>
                    {
                        ["DatabasePath"] = dbPath,
                        ["ObjectsPath"] = objectsPath
                    };
                    foreach (var (key, value) in extraConfiguration ?? new Dictionary<string, string?>())
                    {
                        values[key] = value;
                    }

                    configuration.AddInMemoryCollection(values);
                });
            });
    }
}
