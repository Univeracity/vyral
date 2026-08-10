# Execution Runtime Limitations

`Vyral.Execution` is the portable contract for durable work. It does not make a managed provider's
delivery, identity, storage, or operations behavior portable. Consumers should inspect adapter
capabilities, operational policy, and resume policy before relying on an optional feature.

## Portable guarantees

The reviewed baseline is durable runs, cancellation, retries, artifacts, trace history, and
idempotency. Timers, external events, durable waits, leases, restart recovery, and external-worker
execution are capability-gated. A handler can run more than once after recovery or message
redelivery; side effects before a checkpoint or durable wait must therefore be idempotent.

Payloads, results, checkpoints, artifacts, status details, and trace events are bounded. The
runtime persists plugin-owned JSON but does not validate domain schemas, interpret checkpoints, or
provide domain-level transaction semantics.

## Adapter boundaries

The local SQLite adapter is for local-first use and a single local deployment. Azure Durable
Functions, AWS DynamoDB/SQS, and Google Firestore/Cloud Tasks adapters preserve the portable
protocol while retaining provider-specific deployment requirements. Their queues and schedulers are
at-least-once delivery mechanisms; workers must claim durable state before acting on a message.

An adapter can expose only external workers or only in-process handlers. Plugins must inspect the
execution-model capabilities and must not assume that registering a .NET handler makes it runnable
by an external-worker adapter. An external worker's lease token is an opaque bearer secret, not a
caller-authentication mechanism; hosts must authenticate the worker before granting protocol
access.

`Vyral.Execution.WorkerClient.ExecutionPluginWorker` can load the same ordinary plugin package in
an external process and map progress, trace events, artifacts, checkpoints, completion, and durable
wait replay through the portable worker protocol. That protocol does not currently expose
handler-side coordination leases, standalone timer creation, or raising an external event from the
worker context. Such calls fail explicitly; plugins that require them need an adapter with
`in_process.handlers`. This is an execution-model boundary, not a provider-specific workaround.

Managed deployments own their resource configuration: identity and authorization, encryption,
backups, retention, queue limits, monitoring, scheduler cadence, and disaster recovery. Remote
adapters that expose maintenance use reconciliation to recover an interrupted durable-state/queue
enqueue boundary; operators run that maintenance from a trusted recurring job. Retention and
recovery policies remain deployment decisions rather than portable plugin behavior.

## Live qualification

The shared conformance suite proves the provider-neutral lifecycle. Opt-in cloud gates add evidence
for caller-controlled temporary resources; they do not qualify a consumer's production account,
identity policy, worker code, or operational runbook. The Google gate deliberately pauses its
temporary queue while checking Firestore state and Cloud Tasks OIDC construction, so a fully
disposable worker smoke check remains the appropriate deployment-level delivery qualification.

Use the local, package-consumer, and provider-specific gates listed in the package READMEs before
adopting a new runtime version. Treat a failed or skipped live gate as missing operational evidence,
not as a portable-contract failure.
