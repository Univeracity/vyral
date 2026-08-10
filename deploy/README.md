# Vyral Google Cloud Run Deployment

This directory captures a reference shared-services deployment shape for Vyral
on Google Cloud.

The intended production boundary is:

```text
Product-specific Cloud Run services
  -> authenticated Vyral HTTP ingest
      -> Vyral Cloud Run
      -> Firestore records and traces
      -> Cloud Storage raw artifacts
      -> Cloud Tasks result processing
```

The Vyral project should not be named or configured as though it belongs to one
consumer. Product services remain in their own projects; Vyral is the shared
evidence and control plane.

## Target project

```text
your-gcp-project-id
```

Set `VYRAL_PROJECT_ID` to a dedicated or intentionally shared Google Cloud
project. The provisioning script requires the matching `VYRAL_CONFIRM_DEPLOY`
value before it creates resources.

Use this project for:

- `vyral-server` Cloud Run
- Vyral Artifact Registry images
- Vyral Firestore database
- Vyral Cloud Storage artifact bucket
- Vyral Cloud Tasks queues selected and provisioned by an execution adapter
- Vyral API-key secrets

Keep product-specific workloads, public delivery, and data collection in the
product project.

## Required Resources

The provisioning script creates or verifies:

- Artifact Registry Docker repository
- runtime service account
- Cloud Storage artifact bucket
- Firestore native database
- Secret Manager secret for the Vyral API key
- Cloud Run service

It does not create Cloud Tasks queues or configure Cloud Tasks OIDC callback
identities. Those are execution-adapter or consumer-owned resources; use the
adapter's deployment material and read-only preflight to provision and verify
them explicitly.

The preferred production posture is IAM-only Cloud Run service-to-service
access. Product services call the Google-managed Vyral service URL with a
Google-issued identity token, and Vyral may still require `X-Vyral-Api-Key` or
an Authorization bearer token as defense-in-depth.

A public Google Front End service protected only by Vyral API keys remains
available as an explicit transitional mode for low-sensitivity migrations. Do
not use that posture for sensitive workloads.

## Product Onboarding Checklist

Before adding a product to shared Vyral, record:

- product id
- environment
- allowed routes
- allowed collections and partition keys
- object prefix or artifact bucket convention
- Cloud Tasks queue and retry policy when needed
- API key secret or service account
- quota/admission policy
- labels for cost attribution
- owner/contact for incidents
- schema version and schema hash requirements

Do not onboard a product with another product's key, queue, or collection.

## Run

From the Vyral repository root:

```bash
VYRAL_PROJECT_ID=your-gcp-project-id \
VYRAL_CONFIRM_DEPLOY=your-gcp-project-id \
VYRAL_BILLING_ACCOUNT=your-billing-account-id \
VYRAL_CLOUD_RUN_AUTH_MODE=iam-internal \
VYRAL_INVOKER_SERVICE_ACCOUNTS=product-runtime@product-project.iam.gserviceaccount.com \
VYRAL_API_KEY_SOURCE_PROJECT=product-project \
deploy/provision-gcp-shared-vyral.sh
```

`VYRAL_CLOUD_RUN_AUTH_MODE` supports:

- `iam-internal`, the default and preferred production mode. The script deploys
  Cloud Run with `--no-allow-unauthenticated`, removes public invoker bindings,
  and grants `roles/run.invoker` to each comma-separated account in
  `VYRAL_INVOKER_SERVICE_ACCOUNTS`.
- `public-api-key`, a transitional mode that deploys with
  `--allow-unauthenticated` and relies on Vyral's API-key middleware for
  protected routes.

Execution adapters configure their own queues, callback identities, and
callback routes through their explicit adapter settings. Provision and preflight
those resources with the generic execution guidance below; do not infer a
consumer's queue or callback policy from the shared Vyral deployment.

`VYRAL_INVOKER_SERVICE_ACCOUNTS` should contain only specific product runtime
principals in IAM-only mode, such as
`serviceAccount:product-runtime@PROJECT_ID.iam.gserviceaccount.com` or the
email form without the `serviceAccount:` prefix. The script rejects `allUsers`
and `allAuthenticatedUsers` in IAM-only mode; use `public-api-key` when a
public deployment is intentional.

If no existing API-key secret should be copied, provide a value directly:

```bash
VYRAL_PROJECT_ID=your-gcp-project-id \
VYRAL_CONFIRM_DEPLOY=your-gcp-project-id \
VYRAL_CLOUD_RUN_AUTH_MODE=iam-internal \
VYRAL_INVOKER_SERVICE_ACCOUNTS=product-runtime@product-project.iam.gserviceaccount.com \
VYRAL_API_KEY_VALUE="replace-with-generated-key" \
deploy/provision-gcp-shared-vyral.sh
```

Do not commit API key values.

The script refuses to run unless `VYRAL_PROJECT_ID` is set and
`VYRAL_CONFIRM_DEPLOY` exactly matches it. This is intentional: the active
project is a shared services plane, not a disposable test surface.

By default the script builds the committed `HEAD` tree, not the dirty working
tree. Set `VYRAL_BUILD_REF=working-tree` only when intentionally deploying
uncommitted local changes.

To preserve additional product keys on deploy, provide a comma-separated list
of Secret Manager names:

```bash
VYRAL_PROJECT_ID=your-gcp-project-id \
VYRAL_CONFIRM_DEPLOY=your-gcp-project-id \
VYRAL_EXTRA_API_KEY_SECRETS=vyral-api-key-product-example \
deploy/provision-gcp-shared-vyral.sh
```

The primary `VYRAL_API_KEY_SECRET` is the primary Vyral API key. Extra keys are
exposed to Vyral as indexed
`Server__ApiKeys__N` entries. For IAM-only product integrations, use distinct
per-product service accounts and distinct per-product API keys unless a route
is intentionally shared.

The runtime service account receives project-level access only for Firestore,
Cloud Tasks, logging, and monitoring. Object access is granted on the configured
bucket, and Secret Manager access is granted on the specific API-key secrets
bound into Cloud Run. Product runtime accounts should receive `roles/run.invoker`
only; they should not receive direct Firestore, Cloud Storage, Cloud Tasks, or
Secret Manager access in the Vyral project.

## Durable Execution Workers

The Firestore/Cloud Tasks execution adapter is an opt-in shared-plane feature. Do
not enable `ExecutionRuntime__Adapter=google-firestore-cloud-tasks` without all
of the following:

- A dedicated Cloud Tasks queue and Firestore execution root per environment.
- A Google Cloud Storage artifact container when external workers may write artifacts larger than
  256 KiB. The Google adapter keeps payloads, results, checkpoints, and inline artifacts below a
  conservative Firestore envelope limit; it offloads larger artifacts only to the explicitly
  configured `ExecutionRuntime__Google__ArtifactObjectContainer`.
- An explicit `WorkerRoutes` entry for every external handler. A handler may not
  inherit another product's worker merely because it has the same queue.
- A product runtime service account and a worker service account with only
  `roles/run.invoker` on Vyral; the Vyral runtime account alone receives
  Firestore and Cloud Tasks permissions.
- `Server__ExecutionAccess` Google-OIDC policies for each caller and worker,
  plus matching `ExecutionRuntime__ProductPolicies` entries that bind worker ids
  to product scopes. API keys by themselves are not a multi-consumer execution
  identity boundary.
- An OIDC-authenticated maintenance principal (normally Cloud Scheduler calling
  a small Cloud Run job) that invokes both `/execution/runtime/maintenance/prune`
  and `/execution/runtime/maintenance/reconcile`. Reconciliation idempotently
  redrives Firestore-runnable work after a state-write/Cloud-Tasks-enqueue
  interruption. The Google adapter recursively removes terminal run-owned state;
  Firestore TTL alone does not clean nested subcollections.

The application verifies a Google-issued token against the Vyral Cloud Run URL,
then overwrites the request's service identity with the verified service-account
email. It rejects unscoped starts, worker-id impersonation, un-routed handlers,
and maintenance calls without a dedicated policy. Keep test-only public worker
fixtures out of the shared project.

### Execution deployment preflight

Run the read-only preflight after deploying candidate Vyral and worker Cloud Run services, but
before routing consumer traffic to the execution plane. It checks the candidate environment file,
Firestore database/root separation, Cloud Tasks queue, explicit handler routes, Cloud Run service
identities, `run.invoker`, Firestore/Cloud Tasks/GCS roles, Cloud Tasks OIDC minting permissions,
and the Vyral worker/product-policy bindings. It never creates, updates, queues, deploys, or
deletes a resource.

```bash
VYRAL_EXECUTION_PROJECT_ID=your-gcp-project-id \
VYRAL_EXECUTION_SERVER_SERVICE=vyral-server \
VYRAL_EXECUTION_WORKER_SERVICE=product-example-worker \
VYRAL_EXECUTION_WORKER_ID=product-example-worker \
VYRAL_EXECUTION_HANDLER_IDS=product.example.job \
VYRAL_EXECUTION_CONFIG_FILE=deploy/google-cloud-run.env \
deploy/preflight-google-execution.sh
```

The candidate file uses the same `KEY=value` format as
`deploy/google-cloud-run.env.example`. The script reports every failed invariant and exits
non-zero; it does not conceal a partial or unsafe deployment state.

## Product cutover

An integration owner selects its routes, collections, callback policy, and
provenance verifier configuration. In IAM-only mode, its runtime service
account needs `roles/run.invoker` on Vyral and must call the Vyral URL with a
Google ID token. Retain a prior service until the integration owner's complete
acceptance and query path has been verified.

## Multi-Project Posture

Vyral stores product-owned artifacts and records, but products should not receive
direct IAM access to Vyral storage resources. The HTTP ingest contract is the
boundary.

Recommended metadata for every product integration:

```text
productId
environment
schemaVersion
schemaHash
sourceService
sourceProject
tenantId or accountId when available
testId, runId, sessionId, or requestId
usageClass or accountTier when available
```

A product integration can map its results to a stable record type. Make product
identity explicit rather than relying on hostname or route alone. Product-owned
compatibility deployment overlays belong with their respective products, not in
this provider-neutral directory.
