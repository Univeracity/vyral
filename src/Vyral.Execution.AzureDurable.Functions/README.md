# Vyral.Execution.AzureDurable.Functions

`Vyral.Execution.AzureDurable.Functions` is the optional Microsoft Durable Task bridge for the
SDK-neutral `Vyral.Execution.AzureDurable` package. It keeps the replayed orchestrator free of
status-store I/O and handler execution: it calls one start activity and one persisted step activity
instead.

A Functions application owns trigger attributes, HTTP authentication, dependency injection,
registered Vyral handlers, task-hub settings, and deployment topology. Its function methods call:

```csharp
// HTTP trigger: receive DurableTaskClient and start through the scheduler bridge.
var client = AzureDurableFunctionsBridge.CreateClient(vyralHost, durableTaskClient);
var run = await client.StartRunAsync(command, cancellationToken);

// Orchestrator trigger: receive TaskOrchestrationContext and command input.
return await AzureDurableFunctionsBridge.OrchestrateAsync(vyralHost, context, command);

// Start and step activity triggers respectively:
return await AzureDurableFunctionsBridge.StartActivityAsync(vyralHost, command);
return await AzureDurableFunctionsBridge.StepActivityAsync(vyralHost, command);
```

The bridge supports the portable `durable.waits` capability. A handler-side timer or external-event
wait registers its Vyral state in the step activity and suspends that activity. The replayed
orchestrator then uses `CreateTimer` or `WaitForExternalEvent`; its next step activity atomically
persists the wake outcome before rerunning the handler. Handlers must checkpoint non-idempotent
work before waiting, since the handler body is replayed from its beginning after a wake.

Starting is resilient to the small gap between reserving a Vyral run and submitting its provider
orchestration. An idempotent replay of an active run calls the scheduler again. The Functions
scheduler deduplicates Durable instances in `Pending` and `Running` states, so a confirmed or
ambiguous prior submission remains one orchestration while an earlier failed submission is retried.
