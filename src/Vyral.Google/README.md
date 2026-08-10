# Vyral.Google

`Vyral.Google` provides optional Google Cloud record, object, trace, and external-worker execution
adapters. The portable execution contract remains independent of Firestore, Cloud Tasks, and Cloud
Run behavior.

## Live execution qualification

The Google execution gate creates a temporary paused Cloud Tasks queue and uniquely prefixed
Firestore roots, validates Cloud Tasks OIDC task construction, and runs the shared external-worker
conformance regime against Firestore. It deletes the queue and its run-owned Firestore state after
the run. A production-adjacent project can instead supply a dedicated named database and reusable
paused queue. Supply a caller-controlled Cloud Run worker URL and service account; credentials are
read from the active Application Default Credential chain or an explicitly injected short-lived
access token and are never printed.

```bash
VYRAL_GOOGLE_EXECUTION_PROJECT_ID=your-gcp-project-id \
VYRAL_GOOGLE_EXECUTION_WORKER_URL=https://your-worker-url/tasks/execution \
VYRAL_GOOGLE_EXECUTION_SERVICE_ACCOUNT_EMAIL=task-dispatcher@your-gcp-project-id.iam.gserviceaccount.com \
scripts/validate-google-execution-live.sh
```

Optional isolation inputs are `VYRAL_GOOGLE_EXECUTION_DATABASE_ID` and
`VYRAL_GOOGLE_EXECUTION_EXISTING_QUEUE`. A reusable queue must use the
`vyral-it-google-exec-` prefix and already be paused; the gate purges it after every run but never
deletes it. `VYRAL_GOOGLE_EXECUTION_ACCESS_TOKEN` and `VYRAL_GOOGLE_LIVE_ACCESS_TOKEN` accept
short-lived tokens for execution and shared Google live tests respectively.

The manual `Google Live Qualification` workflow uses the environment-scoped
`google-live-qualification` secrets and GitHub workload identity federation. Configure its
`GOOGLE_LIVE_*` environment secrets with the isolated named database, paused queue, worker URL,
project, qualification service account, and workload identity provider. It uploads a redacted,
commit-bound receipt and never uses a service-account key file. A passing operational receipt does
not automatically promote the adapter's checked-in qualification level.

The task queue is paused during this gate to prevent a qualification task from invoking a shared
worker. Use the disposable Go smoke worker for a deliberate end-to-end Cloud Run delivery test;
the portable contract is validated independently from that provider-specific delivery path.

## Contention and object preconditions

`FirestoreExecutionStateStore` gives admission and lease transactions an explicit bounded retry
budget because those operations intentionally contend on capacity counters and lease documents.
The default is 20 attempts and callers may configure a value from 1 through 50:

```csharp
var state = new FirestoreExecutionStateStore(
    firestore,
    "vyral_execution",
    new FirestoreExecutionStateStoreOptions
    {
        ContentionTransactionMaxAttempts = 20
    });
```

If Firestore still exhausts that budget for a competitive lease claim, only the provider's
`ABORTED` result is normalized to “not acquired”; authorization, validation, and other failures
remain visible. This preserves the portable lease-admission contract while allowing another worker
to win safely.

`CloudStorageObjectStore` exposes GCS generations as quoted Vyral ETags. Conditional writes and
deletes require a usable generation ETag and fail closed instead of silently becoming unconditional.
`If-None-Match` supports the portable `*` create-only form. Object listing applies and returns native
GCS continuation tokens while enforcing the requested page bound.
