using System.Security.Cryptography;
using System.Text;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Tests.Local;

public sealed class ExecutionAdmissionTests
{
    [Fact]
    public void Create_IsStableAndDoesNotReflectTheRawIdempotencyKey()
    {
        var run = CreateRun();

        var first = ExecutionAdmission.Create(run, "startExecutionRun", "/execution/runs/run-1");
        var second = ExecutionAdmission.Create(run, "startExecutionRun", "/execution/runs/run-1");

        Assert.Equal(AdmissionReceipt.CurrentVersion, first.Version);
        Assert.Equal(first.AdmissionId, second.AdmissionId);
        Assert.Equal("accepted", first.Status);
        Assert.Equal("run-1", first.ResourceId);
        Assert.Equal("payload-sha256", first.RequestHash);
        Assert.Equal(Sha256("customer-secret"), first.IdempotencyKeyHash);
        Assert.DoesNotContain("customer-secret", System.Text.Json.JsonSerializer.Serialize(first));
        Assert.False(first.Replayed);
    }

    [Fact]
    public void Create_ReportsReplayAndDurableRejectionEvidence()
    {
        var run = CreateRun();
        run.Status = ExecutionRunStatuses.Rejected;
        run.FailureClass = ExecutionFailureClasses.QueueFull;
        run.Error = "queue full";
        run.AdmissionReplayed = true;

        var receipt = ExecutionAdmission.Create(run, "startExecutionRun", "/execution/runs/run-1");

        Assert.Equal(AdmissionStatuses.Rejected, receipt.Status);
        Assert.True(receipt.Replayed);
        Assert.Equal(ExecutionFailureClasses.QueueFull, receipt.FailureClass);
        Assert.Equal("queue full", receipt.Error);
    }

    private static ExecutionRun CreateRun() => new()
    {
        Id = "run-1",
        HandlerId = "test.handler",
        Status = ExecutionRunStatuses.Queued,
        PayloadHash = "payload-sha256",
        IdempotencyKey = "customer-secret",
        CreatedAtUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
        UpdatedAtUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc)
    };

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
