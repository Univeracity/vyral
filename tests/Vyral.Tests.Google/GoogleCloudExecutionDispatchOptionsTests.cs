namespace Vyral.Tests.Google;

public sealed class GoogleCloudExecutionDispatchOptionsTests
{
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
