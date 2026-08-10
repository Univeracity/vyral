using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.DynamoDBv2;
using Amazon.SQS;
using Amazon.SQS.Model;
using Vyral.Execution;
using Vyral.Execution.Aws;

namespace Vyral.Tests.Aws;

/// <summary>
/// Opt-in deployment check for DynamoDB transactions and an actual SQS transport. It uses a
/// unique table and temporary queue supplied by the gate, and deliberately executes no handler
/// code in the test process.
/// </summary>
public sealed class AwsDynamoExecutionLiveTests
{
    [AwsExecutionLiveFact]
    public async Task AwsExecutionRuntime_UsesDynamoDbAndSqsForLeaseWaitAndCompletion()
    {
        var tableName = AwsLiveSettings.ExecutionDynamoDbTable!;
        var queueUrl = AwsLiveSettings.ExecutionSqsQueueUrl!;
        if (!tableName.StartsWith("vyral-it-aws-exec-", StringComparison.Ordinal))
            throw new InvalidOperationException("Live AWS execution tests require a uniquely prefixed 'vyral-it-aws-exec-' DynamoDB table.");

        var dynamo = new AmazonDynamoDBClient();
        var sqs = new AmazonSQSClient();
        var state = new DynamoDbExecutionStateStore(dynamo, new DynamoDbExecutionStateStoreOptions
        {
            TableName = tableName,
            Root = AwsLiveSettings.UniquePrefix("vyral-live-execution")
        });
        var dispatch = new AwsSqsExecutionDispatcher(
            new AwsSqsExecutionQueue(sqs),
            new AwsSqsExecutionDispatchOptions { QueueUrl = queueUrl });
        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "handoff.aws.execution.worker",
            PluginId = "handoff.aws.execution",
            DisplayName = "Temporary AWS execution handoff worker"
        };
        var runtime = new AwsDynamoExecutionRuntimeAdapter(
            state,
            dispatch,
            new AwsDynamoExecutionRuntimeOptions
            {
                MaxActiveRuns = 1,
                MaxListLimit = 10,
                DefaultListLimit = 10,
                MaxHistoryLimit = 10,
                DefaultHistoryLimit = 10,
                WorkerDispatchers =
                [
                    new AwsDynamoExecutionWorkerDispatcher { HandlerId = handler.HandlerId, Dispatcher = dispatch }
                ]
            });
        runtime.RegisterExternalHandler(handler);
        ExecutionRun? created = null;
        ExecutionRun? scheduled = null;

        try
        {
            created = await runtime.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = handler.HandlerId,
                Payload = new JsonObject { ["source"] = "live-handoff" }
            });
            Assert.Equal(ExecutionRunStatuses.Queued, created.Status);
            Assert.Equal(1, await state.GetActiveRunCountAsync());

            var dispatched = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"]
            });
            var message = Assert.Single(dispatched.Messages);
            var envelope = JsonSerializer.Deserialize<AwsSqsExecutionDispatchMessage>(message.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(envelope);
            Assert.Equal(created.Id, envelope.RunId);
            Assert.Equal(ExecutionDispatchReasons.RunReady, envelope.Reason);
            Assert.Equal("1", message.MessageAttributes["vyral_execution_dispatch"].StringValue);
            await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle);

            var claims = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
            {
                WorkerId = "handoff-live-worker",
                HandlerIds = { handler.HandlerId },
                RunId = created.Id,
                TtlSeconds = 60
            })));
            var lease = Assert.IsType<ExecutionExternalWorkerLease>(Assert.Single(claims, item => item is not null));
            await runtime.CheckpointExternalLeaseAsync(new ExecutionExternalWorkerCheckpointRequest
            {
                LeaseKey = lease.LeaseKey,
                LeaseToken = lease.LeaseToken,
                WorkerId = lease.WorkerId,
                Checkpoint = new ExecutionCheckpointWrite { Key = "live-progress", Content = new JsonObject { ["position"] = 1 } }
            });

            var suspended = await runtime.WaitExternalLeaseAsync(new ExecutionExternalWorkerWaitRequest
            {
                LeaseKey = lease.LeaseKey,
                LeaseToken = lease.LeaseToken,
                WorkerId = lease.WorkerId,
                Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
                Name = "approval",
                TimeoutAtUtc = DateTime.UtcNow.AddMinutes(1)
            });
            Assert.True(suspended.Suspended);

            await runtime.RaiseEventAsync(new ExecutionExternalEventRequest
            {
                RunId = created.Id,
                Name = "approval",
                Payload = new JsonObject { ["approved"] = true }
            });
            var resumed = Assert.IsType<ExecutionExternalWorkerLease>(await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
            {
                WorkerId = "handoff-live-worker",
                HandlerIds = { handler.HandlerId },
                RunId = created.Id,
                TtlSeconds = 60
            }));
            var outcome = await runtime.WaitExternalLeaseAsync(new ExecutionExternalWorkerWaitRequest
            {
                LeaseKey = resumed.LeaseKey,
                LeaseToken = resumed.LeaseToken,
                WorkerId = resumed.WorkerId,
                Kind = ExecutionExternalWorkerWaitKinds.ExternalEvent,
                Name = "approval",
                TimeoutAtUtc = DateTime.UtcNow.AddMinutes(1)
            });
            Assert.False(outcome.Suspended);
            Assert.True(outcome.Outcome!.Event!.Payload!["approved"]!.GetValue<bool>());

            var completed = await runtime.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
            {
                LeaseKey = resumed.LeaseKey,
                LeaseToken = resumed.LeaseToken,
                WorkerId = resumed.WorkerId,
                Result = ExecutionRunResult.Succeeded()
            });
            Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
            Assert.Equal(0, await state.GetActiveRunCountAsync());

            // The event wake-up above deliberately leaves an at-least-once dispatch envelope
            // visible. Drain it before proving that a real SQS delivery-delay timer is received
            // and leaseable after its due time.
            await DrainVisibleMessagesAsync(sqs, queueUrl);
            scheduled = await runtime.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = handler.HandlerId,
                Payload = new JsonObject { ["source"] = "live-scheduled-timer" },
                ScheduledAtUtc = DateTime.UtcNow.AddSeconds(2)
            });
            Assert.Equal(ExecutionRunStatuses.Waiting, scheduled.Status);
            var scheduledDelivery = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 8
            });
            var scheduledMessage = Assert.Single(scheduledDelivery.Messages);
            var scheduledEnvelope = JsonSerializer.Deserialize<AwsSqsExecutionDispatchMessage>(scheduledMessage.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(scheduledEnvelope);
            Assert.Equal(scheduled.Id, scheduledEnvelope.RunId);
            Assert.NotNull(scheduledEnvelope.ScheduledAtUtc);
            await sqs.DeleteMessageAsync(queueUrl, scheduledMessage.ReceiptHandle);

            var scheduledLease = Assert.IsType<ExecutionExternalWorkerLease>(await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
            {
                WorkerId = "handoff-live-worker",
                HandlerIds = { handler.HandlerId },
                RunId = scheduled.Id,
                TtlSeconds = 60
            }));
            var scheduledCompletion = await runtime.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
            {
                LeaseKey = scheduledLease.LeaseKey,
                LeaseToken = scheduledLease.LeaseToken,
                WorkerId = scheduledLease.WorkerId,
                Result = ExecutionRunResult.Succeeded()
            });
            Assert.Equal(ExecutionRunStatuses.Succeeded, scheduledCompletion.Status);
        }
        finally
        {
            if (scheduled is not null)
            {
                var current = await state.GetRunAsync(scheduled.Id, includeResult: false);
                if (current is not null) await state.DeleteRunAsync(current);
            }
            if (created is not null)
            {
                var current = await state.GetRunAsync(created.Id, includeResult: false);
                if (current is not null) await state.DeleteRunAsync(current);
            }
        }
    }

    [AwsExecutionLiveFact]
    public async Task AwsExecutionRuntime_ReconcilesLongDelayFromDynamoDbIntoSqs()
    {
        var tableName = AwsLiveSettings.ExecutionDynamoDbTable!;
        var queueUrl = AwsLiveSettings.ExecutionSqsQueueUrl!;
        if (!tableName.StartsWith("vyral-it-aws-exec-", StringComparison.Ordinal))
            throw new InvalidOperationException("Live AWS execution tests require a uniquely prefixed 'vyral-it-aws-exec-' DynamoDB table.");

        var dynamo = new AmazonDynamoDBClient();
        var sqs = new AmazonSQSClient();
        var state = new DynamoDbExecutionStateStore(dynamo, new DynamoDbExecutionStateStoreOptions
        {
            TableName = tableName,
            Root = AwsLiveSettings.UniquePrefix("vyral-live-long-delay")
        });
        var dispatch = new AwsSqsExecutionDispatcher(
            new AwsSqsExecutionQueue(sqs),
            new AwsSqsExecutionDispatchOptions { QueueUrl = queueUrl, MaximumDelaySeconds = 0 });
        var handler = new ExecutionHandlerDescriptor
        {
            HandlerId = "handoff.aws.execution.long-delay.worker",
            PluginId = "handoff.aws.execution.long-delay",
            DisplayName = "Temporary AWS long-delay maintenance worker"
        };
        var runtime = new AwsDynamoExecutionRuntimeAdapter(
            state,
            dispatch,
            new AwsDynamoExecutionRuntimeOptions
            {
                WorkerDispatchers =
                [
                    new AwsDynamoExecutionWorkerDispatcher { HandlerId = handler.HandlerId, Dispatcher = dispatch }
                ]
            });
        runtime.RegisterExternalHandler(handler);
        ExecutionRun? created = null;

        try
        {
            created = await runtime.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = handler.HandlerId,
                Payload = new JsonObject { ["source"] = "live-long-delay-maintenance" },
                ScheduledAtUtc = DateTime.UtcNow.AddSeconds(2)
            });
            Assert.Equal(ExecutionRunStatuses.Waiting, created.Status);

            var initiallyVisible = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 0
            });
            Assert.Empty(initiallyVisible.Messages ?? []);

            await Task.Delay(TimeSpan.FromSeconds(3));
            AwsSqsExecutionDispatchMessage? envelope = null;
            for (var attempt = 0; attempt < 8 && envelope is null; attempt++)
            {
                var reconciled = await runtime.ReconcileDispatchAsync(new ExecutionMaintenanceDispatchReconcileRequest { Limit = 10 });
                Assert.Empty(reconciled.Failures);
                var delivered = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 1
                });
                foreach (var message in delivered.Messages ?? [])
                {
                    var candidate = JsonSerializer.Deserialize<AwsSqsExecutionDispatchMessage>(message.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle);
                    if (candidate?.RunId == created.Id) envelope = candidate;
                }

                if (envelope is null) await Task.Delay(TimeSpan.FromMilliseconds(250));
            }

            Assert.NotNull(envelope);
            Assert.Equal(created.Id, envelope!.RunId);
            Assert.Equal(ExecutionDispatchReasons.TimerDue, envelope.Reason);

            var lease = Assert.IsType<ExecutionExternalWorkerLease>(await runtime.LeaseNextRunAsync(new ExecutionExternalWorkerLeaseRequest
            {
                WorkerId = "handoff-live-long-delay-worker",
                HandlerIds = { handler.HandlerId },
                RunId = created.Id,
                TtlSeconds = 60
            }));
            var completed = await runtime.CompleteExternalLeaseAsync(new ExecutionExternalWorkerCompletionRequest
            {
                LeaseKey = lease.LeaseKey,
                LeaseToken = lease.LeaseToken,
                WorkerId = lease.WorkerId,
                Result = ExecutionRunResult.Succeeded()
            });
            Assert.Equal(ExecutionRunStatuses.Succeeded, completed.Status);
            Assert.Equal(0, await state.GetActiveRunCountAsync());
        }
        finally
        {
            if (created is not null)
            {
                var current = await state.GetRunAsync(created.Id, includeResult: false);
                if (current is not null) await state.DeleteRunAsync(current);
            }
        }
    }

    private static async Task DrainVisibleMessagesAsync(IAmazonSQS sqs, string queueUrl)
    {
        while (true)
        {
            var messages = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 0
            });
            if (messages.Messages is not { Count: > 0 } visible) return;
            foreach (var message in visible)
            {
                await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle);
            }
        }
    }
}
