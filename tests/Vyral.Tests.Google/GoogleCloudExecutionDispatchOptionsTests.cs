namespace Vyral.Tests.Google;

public sealed class GoogleCloudExecutionDispatchOptionsTests
{
    [Fact]
    public void DispatchMessage_UsesThePortableCamelCaseJsonContract()
    {
        const string json = """
            {"runId":"run-1","reason":"run_ready","scheduledAtUtc":null}
            """;

        var message = System.Text.Json.JsonSerializer.Deserialize<GoogleCloudExecutionDispatchMessage>(
            json,
            Vyral.Execution.ExecutionJson.Options);

        Assert.NotNull(message);
        Assert.Equal("run-1", message!.RunId);
        Assert.Equal("run_ready", message.Reason);
    }

    [Fact]
    public void Validate_RequiresGoogleQueueAndCloudRunTarget()
    {
        Assert.Throws<InvalidOperationException>(() => new GoogleCloudExecutionDispatchOptions().Validate());
        Assert.Throws<InvalidOperationException>(() => new GoogleCloudExecutionDispatchOptions
        {
            ProjectId = "project",
            LocationId = "us-central1",
            QueueId = "execution",
            WorkerUrl = "not-a-url"
        }.Validate());
    }

    [Fact]
    public void Validate_AcceptsCloudRunOidcConfiguration()
    {
        var options = new GoogleCloudExecutionDispatchOptions
        {
            ProjectId = "project",
            LocationId = "us-central1",
            QueueId = "execution",
            WorkerUrl = "https://worker-abc-uc.a.run.app/execution/dispatch",
            ServiceAccountEmail = "execution-dispatch@project.iam.gserviceaccount.com",
            OidcAudience = "https://worker-abc-uc.a.run.app"
        };

        options.Validate();
    }
}
