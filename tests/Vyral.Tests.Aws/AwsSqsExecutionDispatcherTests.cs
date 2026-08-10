using Vyral.Execution;
using Vyral.Execution.Aws;

namespace Vyral.Tests.Aws;

public sealed class AwsSqsExecutionDispatcherTests
{
    private const string QueueUrl = "https://sqs.us-east-1.amazonaws.com/123456789012/vyral-execution";

    [Fact]
    public async Task Dispatch_EnqueuesOnlyPortableRunEnvelope()
    {
        var queue = new CapturingQueue();
        var dispatcher = new AwsSqsExecutionDispatcher(queue, new AwsSqsExecutionDispatchOptions { QueueUrl = QueueUrl });

        await ((IExecutionRunDispatcher)dispatcher).DispatchAsync(new ExecutionDispatchRequest
        {
            RunId = " run-123 ",
            Reason = ExecutionDispatchReasons.ExternalEvent,
            ScheduledAtUtc = DateTime.UtcNow.AddSeconds(30)
        });

        var request = Assert.Single(queue.Requests);
        Assert.Equal(QueueUrl, request.QueueUrl);
        Assert.InRange(request.DelaySeconds!.Value, 29, 30);
        Assert.Null(request.MessageGroupId);
        Assert.Null(request.MessageDeduplicationId);
        Assert.Equal("1", request.Attributes["vyral_execution_dispatch"]);

        var message = Assert.IsType<AwsSqsExecutionDispatchMessage>(request.Payload);
        Assert.Equal("run-123", message.RunId);
        Assert.Equal(ExecutionDispatchReasons.ExternalEvent, message.Reason);
        Assert.NotNull(message.ScheduledAtUtc);
        Assert.StartsWith("vyral-exec-", message.DispatchId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatch_UsesDeterministicFifoGroupAndDeduplicationId()
    {
        var queue = new CapturingQueue();
        var scheduled = DateTime.UtcNow.AddMinutes(2);
        var dispatcher = new AwsSqsExecutionDispatcher(queue, new AwsSqsExecutionDispatchOptions
        {
            QueueUrl = QueueUrl + ".fifo",
            Fifo = true,
            MessageGroupId = "tenant-a"
        });

        await dispatcher.DispatchAsync("run-123", AwsSqsExecutionDispatchReasons.TimerDue, scheduled);
        await dispatcher.DispatchAsync("run-123", AwsSqsExecutionDispatchReasons.TimerDue, scheduled);

        var first = queue.Requests[0];
        var second = queue.Requests[1];
        Assert.Equal("tenant-a", first.MessageGroupId);
        Assert.Equal(first.MessageDeduplicationId, second.MessageDeduplicationId);
        Assert.Equal(48 + "vyral-exec-".Length, first.MessageDeduplicationId!.Length);
    }

    [Fact]
    public async Task Dispatch_RejectsDelayBeyondSqsMaximumBeforeEnqueue()
    {
        var queue = new CapturingQueue();
        var dispatcher = new AwsSqsExecutionDispatcher(queue, new AwsSqsExecutionDispatchOptions { QueueUrl = QueueUrl });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(
            "run-123",
            scheduleAtUtc: DateTime.UtcNow.AddSeconds(AwsSqsExecutionDispatchOptions.MaximumSupportedDelaySeconds + 30)));

        Assert.Contains("Persist longer timers", exception.Message, StringComparison.Ordinal);
        Assert.Empty(queue.Requests);
    }

    [Fact]
    public void Options_RejectInvalidQueueAndDelayConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() => new AwsSqsExecutionDispatchOptions().Validate());
        Assert.Throws<InvalidOperationException>(() => new AwsSqsExecutionDispatchOptions
        {
            QueueUrl = QueueUrl,
            MaximumDelaySeconds = AwsSqsExecutionDispatchOptions.MaximumSupportedDelaySeconds + 1
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AwsSqsExecutionDispatchOptions
        {
            QueueUrl = QueueUrl + ".fifo",
            Fifo = true,
            MessageGroupId = " "
        }.Validate());
    }

    private sealed class CapturingQueue : IAwsSqsExecutionQueue
    {
        public List<AwsSqsExecutionEnqueueRequest> Requests { get; } = [];

        public Task<AwsSqsExecutionEnqueueResult> EnqueueAsync(AwsSqsExecutionEnqueueRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AwsSqsExecutionEnqueueResult
            {
                MessageId = "test-message",
                QueueUrl = request.QueueUrl,
                DelaySeconds = request.DelaySeconds
            });
        }
    }
}
