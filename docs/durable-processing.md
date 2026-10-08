# Durable processing and recovery

The provider-neutral processing contract runs in the filesystem API and the M6 native Azure Functions host. Both expose durable HTTP acceptance, attempts, configured retries, resume/replay and retention. External RabbitMQ ingestion uses the shared durable acceptance path. The legacy Redis adapter is not a supported durable hosting profile. See [Azure installation](../infra/azure-small/README.md) and [operations/limits](azure-operations.md).

## Acceptance and worker recovery

Deploy an async definition, then POST JSON to its trigger at `/api/v1/trigger/{endpoint}`. The runtime stores the original payload, immutable YAML/script bundle and pending step state before returning **202 Accepted** with `executionId`, `revisionId` and a `Location` pointing to its authenticated execution resource. This acknowledges acceptance, not downstream completion. Persistence failures must not produce a successful acceptance response.

Persisted pending steps are the scheduling record. Queue messages wake workers; they are replaceable hints. The recovery worker queries the persistent active-execution index every second (without opening terminal state files), re-enqueues due work, and repairs checkpoint-to-publish gaps after a crash. It does not need the previous process's in-memory queue. `Elwood:Durable:Workers` controls worker count (default 4, range 1–64). `Elwood:Durable:WorkerEnabled=false` disables background work for maintenance/testing while still allowing acceptance.

`IRecoverableStateStore` requires an exclusive claim for the **entire attempt**, including connector I/O and its checkpoint. The reference store uses an operating-system file lock; process exit releases it. Different executions can run concurrently; steps within one execution are serialized. A future lease-based adapter must fence/cancel the old worker before another worker can enter. A plain `IStateStore` is rejected for queued execution. Sync-only embeddings can use legacy adapters, but are not marked durable.

The filesystem adapter flushes temporary files before replacement. Short, separate file-access locks protect reads and replacement, while the whole-attempt claim protects execution ownership. This avoids Windows sharing violations when the portal reads a running execution. Use a persistent local filesystem with working OS locks and same-volume rename; a network share is not a certified distributed deployment. Empty lock files may remain after payload retention. Losing the disk or restoring inconsistent backups is outside a process-crash guarantee.

The original input and revision are immutable. Source result documents are stored per attempt, and the IDM is assembled in dependency-stage and configuration order. Successful pull responses are checkpointed before mapping. Outputs checkpoint their prepared body and each completed destination. Resume therefore avoids repeating a successful earlier call solely because a later transformation or destination failed.

## Retry policy

Sources and outputs accept:

```yaml
retry:
  mode: automatic             # automatic or manual; default automatic
  maxAttempts: 3              # includes the first attempt in this retry cycle
  delayMs: 1000
  backoffMultiplier: 2
  maxDelayMs: 60000
  statusCodes: [408, 429, 500, 502, 503, 504]
  transportErrors: true
  allowUncertainWrites: false
```

These are the defaults when a retry object is supplied. Without one, connector failures are not retried automatically. `maxAttempts` must be 1–100, delays 0–86,400,000 ms, backoff 1–10 and listed statuses 400–599. Backoff and next-attempt timestamps are saved so restart does not reset the retry clock. An explicit resume starts a new retry cycle and retains all earlier attempts.

`mode: manual` pauses eligible failures as **Awaiting review**. An operator with recovery permission explicitly requests resume. Parsing, mapping, missing scripts, missing connectors and denied secrets are not treated as transient transport failures. `acceptedStatusCodes` on an HTTP source still means that response is accepted as source data; such a status does not trigger retry.

On transport loss during a possible write, the remote system may have processed the request. Interrupted writes are marked uncertain. Automatic retry of these calls requires both an automatic policy and `allowUncertainWrites: true`; otherwise execution waits for review. HTTP error responses also do not guarantee the remote system rolled back. The maintainer must choose policies that match each receiver's duplicate-handling contract.

`$execution.id`, `$execution.step` and `$execution.revision` are available to step expressions and HTTP string resolution. For example, `Idempotency-Key: '{$execution.id}:crm'` supplies a stable key across attempts of that execution. This only prevents duplicates if the receiver implements idempotency. Replay has a new execution ID; use a business-event key when deduplication must cover replay as well.

For incoming async HTTP requests, an optional `Idempotency-Key` of at most 200 characters identifies one retained execution within a pipeline and trigger source. Matching payloads return its original execution/revision; a different payload returns 409. Concurrent requests can receive 409 while that execution is claimed and should retry their acceptance request. Expired records cannot be reused until cleanup. Retention bounds this facility; it is not a permanent business deduplication ledger.

## Resume and replay

The portal's **Executions** page shows attempt history under **Steps and data → Source/Output → Attempts**. Select a retained live execution to access **Recovery**. Local tests and pre-M4 records are not eligible for live recovery.

| Request | Body | Behavior |
|---|---|---|
| `POST /api/executions/{id}/resume` | `{}` or `{"acknowledgeUncertain":true}` | Resume a failed/awaiting-review execution using saved results and its original revision. |
| `POST /api/executions/{id}/replay` | `{}` | Create a new execution from the original input and revision, linked by `replayOf`. |
| `POST /api/executions/{id}/replay` | `{"revisionId":"published-hash"}` | Replay with an explicitly selected published revision of the same pipeline. |

Both return 202 and an execution ID. They require **`recover` and `read`** permissions scoped to the pipeline. Unauthorized pipeline records return 404; missing operation permission returns 403. Running, expired, deleted and incompatible records return a conflict. Replay requires a terminal execution and a compatible original trigger in the selected revision. The portal requires explicit acknowledgement before replaying real calls and before resuming an uncertain write.

Resume preserves completed steps and destination checkpoints; it does not edit the old revision. If a transformation needs correction, publish it and explicitly replay with that revision. Replay repeats earlier external calls. Neither action performs automatic compensation or a distributed rollback.

New status values extend the existing numeric API enum: `4 = Retrying`, `5 = AwaitingReview`. Each step has `attempts`, `nextAttemptAt` and `outcomeUncertain`. Each attempt records identity, number, timings, status, diagnostics, error, connector calls and an available result reference. Warnings remain successful diagnostics. A process killed during an HTTP call cannot supply response details it never checkpointed.

## Synchronous failures and local tests

Sync and async connector execution share the same step engine. Sync waits for completion or a terminal failure/review state. A synchronous definition may configure:

```yaml
failureResponse:
  statusCode: '502'
  body: '{ executionId: $execution.id, step: $error.step, message: "Processing failed" }'
```

Both fields accept an expression or bundled `.elwood` script. The default status expression is `500`. `$error` contains `step`, `kind`, `messages` and `uncertain`; `$idm` is the data available to the failing step. Diagnostics from these expressions are retained with that step. Response-expression failure is also recorded, and the API returns its generic error response. Async definitions cannot author a synchronous failure response.

Local real/mock tests use the shared engine synchronously, including authored retries, but remain test records. They never become background live work. The supplied-file CLI transformation runner does not call connectors; it validates retry policies and ignores them with a note. HTTP failure-response policies still require a runtime. See [local testing](local-testing.md).

## Retention

`ttlSeconds` is a positive integer, default **2,592,000 seconds (30 days)**. `expiresAt` is set from terminal completion, failure or awaiting-review time. Pending, running and scheduled retry executions do not expire. Resume clears expiry; a later terminal outcome starts a new window.

On startup and every minute, an independent retention worker scans for expiry. Pausing processing with `Elwood:Durable:WorkerEnabled=false` does not pause retention; recovery errors cannot suppress it. Under the same execution claim it writes a deletion tombstone, removes the execution's input, snapshots, results, traces and other documents, then removes its state. Cleanup resumes after interruption. A deletion failure leaves a retryable tombstone and is logged; other eligible executions in the scan still get cleaned. Management reads and recovery exclude tombstoned/expired records. A replay owns copies of its required input, revision and metadata, so deleting the original does not invalidate it.

Existing records without `expiresAt` are not retroactively deleted. The legacy Redis adapter has its historical independent TTL and is not a supported durable adapter. Migration requires draining the old runtime and explicitly deploying bundles into Azure; in-flight conversion is not supplied.

The [storage contract](storage-retention.md) extends the default to delivered Blob outputs, with pipeline defaults and destination overrides. Independent ownership ledgers and Azure lifecycle safeguards enforce it even after execution history expires. Finite execution lifetime, admission bounds and reserved final-state capacity prevent unlimited growth; see [operations](azure-operations.md). Legacy expiry is never silently shortened.

## Verification and boundaries

`DurableExecutionTests` exercises lost queue hints, storage rejection, fresh filesystem instances, duplicate workers, reader/checkpoint concurrency, saved retry schedules, uncertain writes, partial delivery/resume, original/changed revision replay, deterministic merging, retention and authored failure responses.

Build the backend, then run `node --test scripts/runtime-api.test.mjs scripts/durable-runtime.test.mjs`. The durable HTTP test uses isolated state and controlled local endpoints, disables/restarts workers, kills the API during a write, and checks revision pinning, retries, recovery permissions, resume/replay and failure responses. It does not depend on public APIs. Native cloud infrastructure and external message acknowledgement need their own adapter/integration acceptance tests in later milestones.

DSL semantics are unchanged. No exactly-once remote-effect guarantee, source fan-out, new protocol connector, enterprise login or all-cloud readiness is implied by this milestone.

Replay of an execution with any uncertain write requires `acknowledgeUncertain: true`, just like resume; omitting it returns HTTP 409. Production attempt traces contain operation metadata, redacted headers, timings and sizes, with bodies omitted. Source payloads, prepared outputs and delivery receipts remain separate documents referenced by state. Retention runs separately over the terminal index once per minute. A pre-index filesystem store is indexed once on upgrade.


## Synchronous request lifetime

`Elwood:Sync:ExecutionBudgetSeconds` sets the host's total sync execution budget, including connector calls and retry waits (default 60 seconds; positive, at most 300). It also applies to management draft tests, including tests of async definitions. Set it below the deployment gateway's response deadline. The deadline is persisted before execution, and the HTTP request's cancellation and host shutdown are linked to connector cancellation.

Cancellation stops further attempts and deliveries. Budget expiry records an error and HTTP status 504; client/host cancellation records 499 (a disconnected client cannot receive it). In-flight writes are conservatively marked `AwaitingReview`; cancellation does not prove a remote write was undone. No automatic retries continue beyond this lifetime, even if the normal retry policy permits uncertain writes. Explicit acknowledged resume clears the old HTTP deadline and runs detached from the original request. Completed checkpoints remain reusable. Fresh replay creates a distinct execution and may repeat external effects.

The sync owner holds the execution claim across retry waits. Queue hints cannot run a sync execution, and recovery settles an abandoned sync execution when its persisted deadline expires. Existing asynchronous acceptance remains independent of the caller after durable acknowledgement. CPU-bound DSL evaluation and storage checkpoints are cooperative: a budget is not process preemption, but cancellation is checked before each subsequent connector call.

HTTP destination failure messages use a maximum 512-byte JSON preview with credential fields and configured credentials redacted. Oversized (over 64 KiB), invalid or unstructured response bodies are omitted from errors. Full payload documents and explicit local-test body captures follow their separate inspection policy. Execution errors never embed an unbounded rejected response body.
