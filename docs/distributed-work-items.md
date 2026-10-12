# Distributed work items and Redis coordination

Status: **M7 resumed on 12 October 2026 at the owner's request.** The durable-journal package (7.2) passes local and remote CI; the isolated item runner starts 7.3. Host integration, work-item retention/accounting and consumer installation remain required before Eagle adoption. M6's scheduled full-day measurement and temporary-lab cleanup continue separately; no changes are deployed into that measurement. M6 covers the bounded Azure storage profile, not concurrent fan-out within an execution.

The 12 October follow-up makes failure acknowledgement idempotent for the same owner token and decision, and surfaces Redis connection/timeouts as `WorkCoordinationOutcomeUncertainException`. Reconcile a submitted operation using its original identity; never assume a lost reply means no mutation. The foundation's 25 protocol cases subsequently passed against **real Redis 7.4 in CI**, alongside two injected transport tests. See [M6 release evidence](m6-release-evidence.md#fourth-review-installer-verification--12-october-2026). The adapter remains experimental, is not wired into the runtime and is not packable.

## Ownership and release scope

Elwood supplies runtime code, provider adapters, release packages, infrastructure, tutorials and consumer prompts. The Eagle and Deldo project sessions provision and operate their installations. No permanent Azure resources are provisioned by this work package.

Keep the current storage-only profile for compatible low-volume pipelines. Add Redis-backed coordination as a separate, explicit profile; do not silently change existing deployments. The old Redis state adapter does not implement the durable ownership contract and is not a substitute for this work.

## Execution contract

- An execution uses its original immutable pipeline revision. Each dependency stage has a generation, a durable manifest reference, an absolute execution deadline and a finite retention expiry.
- Each item has a stable ordinal in that manifest, an independent attempt number, a unique owner token and a lease. Different items can run concurrently; two owners cannot commit the same attempt. A stage-wide concurrency limit applies across all workers, not separately to each process.
- Redis holds compact coordination data, item status and document references. Large input/output data remains in document storage. No worker rewrites one execution-wide status document for each item update.
- Queue messages remain delivery hints. Work eligibility is persisted before publication. Duplicate claims and duplicate completion acknowledgements must not duplicate completion counters.
- A checkpoint records whether a potentially writing external call is in flight. A per-attempt sequence prevents a delayed checkpoint from reversing a later call status. An expired lease is held for recovery review. An uncertain write requires the authored retry policy or an explicit operator acknowledgement before rescheduling; lease expiry alone never grants permission to repeat an external write.
- Retry requests have stable identities. Repeating a request after losing its response must not schedule another attempt. Completed items cannot be reset by retry.
- Aggregation reads results in manifest order. Only after every item completes may an aggregator claim and publish the stage result. An empty manifest has a valid empty aggregation. Aggregation ownership can expire; only its current token may publish.
- Every Redis operation validates the stage generation. Ordinary operations reject missing or structurally inconsistent coordination data. Recreating an evicted stage as new pending work is forbidden. Registration is only for a newly admitted durable stage, not an automatic fallback on a missing-key error. A coherent rollback to an earlier Redis state cannot be detected by these structural checks alone.
- Redis expiry is absolute and finite; ordinary activity must not keep records forever. The expiry follows the execution's maximum lifetime plus configured history retention. A separate durable manifest/checkpoint journal and reconciliation protocol is required before production activation: replication or persistence alone does not guarantee that every acknowledged mutation survives Redis failover.
- Source fan-out uses the authored path and preserves item identity through fetch, map, retry and aggregation. Output fan-out must be explicit: existing output `path` selects a value processed once, and must not silently acquire different semantics. No DSL engine changes are part of this work.

## Work breakdown

- [x] 7.1 Coordination contract and atomic Redis implementation: item claims, global stage concurrency, fenced checkpoints, duplicate completion, explicit retry, bounded expiry recovery, ordered result inspection/aggregation ownership, finite expiry and structural corruption detection. Foundation protocol cases also pass against real Redis 7.4 in CI; the broader failure/load campaign remains 7.7.
- [x] 7.2 Durable manifests/checkpoints and Redis-loss reconciliation. Immutable admission, fenced item sessions, durable concurrency permits, uncertainty after interrupted writes and reconstruction after lost/rolled-back cache mutations pass local and Docker CI protocol tests. Production load/provider-failover certification remains 7.7, and activation depends on 7.3–7.5.
- [ ] 7.3 **Active:** item execution and the experimental runtime scheduler are implemented. Parent intents, indexed recovery, generation-qualified queue hints and aggregate publication pass local tests; remote verification is pending for the scheduler package. Synchronous real/mock parity and production host registration remain open, with registration dependent on 7.5.
- [ ] 7.4 Add explicit YAML fan-out, bounded scheduling, source aggregation and output fan-out without changing the meaning of existing deployed definitions.
- [ ] 7.5 Item-aware inspection, operator recovery, retention, quotas and backpressure; paginate large execution histories.
- [ ] 7.6 Immutable pipeline/route distribution cache, deployment invalidation and cold-cache correctness.
- [ ] 7.7 Local and real Redis tests: thousands of items across independent workers, duplicate delivery, lost responses, expired owners, restart/failover, downstream uncertainty and retention. Report emulator/protocol tests separately from real Redis tests.
- [ ] 7.8 Consumer Redis infrastructure/configuration, published packages, installation smoke tests and versioned Eagle/Deldo handoffs. Provisioning and cost approval stay in those projects.

## Redis implementation constraints

Atomic scripts must be bounded: claims and checkpoints are constant-sized operations, recovery processes a capped page, and result inspection is paginated. Keys for a stage share one Redis Cluster hash tag. Different stages can live on different slots. Use a dedicated namespace and a non-evicting operational-state policy. A hot single stage can still saturate one shard; partitioning and load measurements are release requirements.

Redis scripting atomicity prevents interleaving, not loss on server failure or rollback of script errors. Validate data and key types before mutation; detect incomplete state and stop further claims. Never report protocol tests as proof of durable failover. See [Redis scripting semantics](https://redis.io/docs/latest/develop/programmability/eval-intro/).

## Current implementation and limits

`IWorkItemCoordinator` is provider-neutral; `Elwood.Pipeline.Redis` implements it using one bounded Lua script. The adapter is experimental, is not packable, and is not registered in any production host. Existing pipeline execution and YAML behavior are unchanged. The standalone coordinator exposes ordered result references; the separate durable journal and item runner below are the authority for connector calls. An explicit scheduler can now connect the runner to `AsyncExecutor` in component tests. Production host registration and expanded-item payload aggregation are not enabled yet.

Each stage has five same-slot keys: header, item records, active lease index, result references and delayed retries. Item records are created on first claim, rather than materializing the entire manifest in a script. Recovery and inspection are capped at 256 items per call. Server time governs leases. Registration accepts a deadline within seven days, leases are 100 ms–2 minutes with renewal, history expiry is finite and at most 365 days after the deadline, and a stage is bounded to 100,000 items / 1,000 concurrent owners / 100 attempts per item. These are protective ceilings, not measured production capacity promises. References are at most 2 KiB and diagnostics at most 1 KiB; callers must redact diagnostics before storage.

Claim and retry request IDs must be retained by their caller and reused after a lost response. Cancellation cannot undo a script already submitted to Redis. Checkpoints require contiguous sequence numbers within an attempt. Completion accepts an identical repeated result but cannot replace it. Retry schedules only failed/review items, guards the expected attempt, preserves its request identity after the next worker claims, and requires acknowledgement of uncertain writes. The host will translate an explicit authored automatic retry policy into that acknowledgement; this adapter never decides whether a destination is safe to repeat.

All normal writes use the same absolute expiry. Durable cleanup of partial script failures, abandoned manifests and document payloads belongs to 7.2/7.5. TTL alone is not the full retention implementation.

### Durable activation gate (7.2)

Before enabling connector dispatch, implement and verify a durable admission/checkpoint protocol that remains authoritative when Redis loses an acknowledged write or rolls back to a structurally valid older state. In particular:

1. Persist immutable stage manifests and their pipeline revision before admitting work. Reconstruction must use those records and item outcomes, never assume that an absent Redis item has not run.
2. Fence durable item ownership and journal the intent to make an external call before sending it. Redis-only lease ownership is insufficient after failover. Preserve uncertain outcomes and the authored automatic/manual retry decision across a restart.
3. Persist completed results before publishing their coordination references. Reconcile completion idempotently after losing either reply, without redoing the external write. Fence aggregation publication against the durable stage generation.
4. Prevent over-admission after loss of Redis's running count. Restoring only completed-item counters cannot establish a safe global concurrency limit while pre-failover workers are still active.
5. Verify full loss, partial loss and coherent rollback during claim, external call, completion, retry and aggregation. Retain the 30-day default and independently tracked output cleanup obligations.

These are the acceptance requirements for the durable journal below. Runtime activation additionally depends on 7.3–7.5 and the consumer profile; the journal is not registered in a production host.

### Durable journal implementation (7.2)

`DurableWorkJournal` stores an immutable stage definition and hash-verified manifest pages, plus one independently fenced record per item. Admission writes every item and concurrency permit before committing the ready marker. Interrupted initialization can resume against the same manifest. Once ready, missing records fail closed; re-admission never recreates a completed/missing item as pending. Inputs, checkpoints and results carry document hashes, so changed or absent referenced content cannot silently change a resumed execution.

Concurrency is enforced by `MaxConcurrency` durable permit records, each held across one item attempt. A permit records its item before that item becomes running. A replacement owner must also acquire/recover the previous item before reusing the permit. Losing Redis's running count, or losing only the permit lease while the item remains held, therefore cannot grant another active attempt. Checkpoints update only their own item record; no execution-wide document is rewritten per item update. The filesystem adapter uses cross-process claims and flushed atomic replacement; the Azure adapter reuses the tested renewable Blob lease fence.

The item session journals an external-call intent before dispatch, then a response/receipt checkpoint before clearing that intent. A lost journal acknowledgement invalidates the session. Recovery retains a possibly writing call as `AwaitingReview`; an interrupted read or post-response transformation becomes `Failed`, retaining its checkpoint. Rescheduling uses the pipeline's explicit retry decision, expected attempt and stable request ID. Completed items cannot be reset. A stage-wide aggregation claim may publish only after every item completes; result pages preserve manifest order, including a valid empty aggregation. Stale aggregation owners cannot publish.

`RedisWorkJournalProjection` is a disposable per-item projection with monotonic durable versions and absolute expiry. Missing entries mean **unknown**, never pending. Full loss, partial loss and a coherent rollback are repaired by bounded durable-page reconciliation. Future dispatch **must acquire `DurableWorkJournal.TryStartAsync`**, even when a cached status says pending; the standalone Redis coordinator from 7.1 is not authority to call a connector. Cache writes happen after durable changes and never authorize another attempt. The generic journal deliberately does not decide which connector writes are safe to retry.

The first implementation favors explicit durable fences over a Redis-only fast path. It precreates one record per item and uses two renewable claims per active attempt; Azure operation counts and throughput must be measured in 7.7 before making Eagle capacity claims. It supports at most 100,000 items, 1,000 durable permits and 100 attempts per item; pages are bounded to 256, admission manifest pages to 128, and journal records to 1 MiB. These remain protective limits, not a measured operating envelope. Absolute execution/history deadlines are enforced; automatic discovery, physical pruning, orphan cleanup, reserved capacity and output-retention integration remain 7.3/7.5. **Neither new store is enabled by the API/Functions host.** Existing deployed YAML and DSL behavior are unchanged.

The journal package (`c7bf6c7`) passed [all CI jobs on 12 October](https://github.com/max-favilli/elwood/actions/runs/38250904402), including real Redis 7.4 projection tests and actual Azurite service leases. At that commit, 18 durable-journal tests, three Azure SDK/fake-transport fencing tests and five journal/projection tests passed. The latter include 300 durably checkpointed items across 16 tasks with eight shared permits, ordered aggregation and full cache reconstruction. Local Garnet checks are additional protocol coverage, not an Azure throughput or managed Redis failover claim.

### Isolated step runner (7.3, in progress)

`WorkItemStepExecutor` freezes the pipeline revision and assembled dependencies once per stage. Each item has a separate input, checkpoint history and durable ownership; shared `ExecutionOperations` perform connector calls and DSL evaluation with `$slice` and `$index` bindings. Ordinary execution does not receive these additional bindings. Output `path` retains its existing meaning; no YAML fan-out behavior is enabled by this package.

Successful source responses are checkpointed before mapping, and delivered destinations retain their receipts. Retrying one item therefore reuses its fetched response or already delivered destinations. The item runner preserves successful mapping warnings, including when an output payload is reused after a delivery retry. It shares failure classification and retry timing with the existing async executor. An unresolved write remains associated with the same call identity while inspection details are added; a new call cannot replace it.

After permit recovery, `RecoverItemAsync` can resubmit a lost retry decision using its original identity. Interrupted external calls require the authored transport retry policy, remaining attempts, and explicit `allowUncertainWrites` for possible writes. Interrupted computation can resume from the checkpoint; a failed DSL map is not treated as a transport fault. Unknown outcomes at the retry limit remain awaiting review. Secrets default to a denying provider; a future host must inject pipeline-scoped access.

The current local runner regressions cover concurrent isolated slices, revision pinning, successful warnings, known rejection/manual retry, uncertain-write approval and retry limits, successful-response reuse, separate destination recovery, cancellation, and lost retry submission both before and after commit. A separate journal regression covers adding inspection details without replacing an unresolved call. These are component tests, not hosted fan-out acceptance. **The API and Functions still use the existing executor.** No new runner is registered until scheduling, parent fencing, inspection, local real/mock parity and finite physical retention are connected.

Verification on 12 October: **265 pipeline tests passed**, including 19 durable-journal tests and 15 item-runner tests; **32 Redis tests passed** against local Garnet plus injected faults. The shared pipeline library builds for .NET 8 and .NET 10. The item-runner commit `9058a5f` passed [all remote CI jobs](https://github.com/max-favilli/elwood/actions/runs/38253373274): 374 Core / 265 Pipeline / 22 CLI / 7 Parquet / 21 connector / 13 hosting / 40 Azure / 2 Redis fault tests, five API contracts, 52 Azure adapter integration tests, 30 real Redis tests, nine real connector contracts, hosted RabbitMQ recovery and the TypeScript suite. Bicep compilation also passed. No cloud resources or deployed lab packages were changed.

The remaining 7.3 integration tasks are tracked separately so component completion cannot be mistaken for hosted readiness:

- [x] Freeze the stage context and execute independently fenced items through shared source/output operations.
- [x] Preserve receipts, captures, warning diagnostics and authored retry decisions across interrupted item attempts.
- [x] Persist the parent's admitted stages and dispatch intents under its execution claim; reject dispatch for stopped, deleting or superseded parents. An already dispatched write can remain uncertain; cancellation cannot undo it.
- [x] Add bounded stage discovery and queue item messages, with recovery after a lost notification and isolation of one corrupt item. Current scheduling is one whole-step item per source/output; explicit expansion remains 7.4.
- [x] Publish the journal's completed stage result to the pinned parent and release dependent stages idempotently, without rewriting parent state on every item checkpoint. Many-item aggregation remains 7.4.
- [ ] Run the same item semantics through synchronous local real/mock testing; mocks must never fall through to a real connector.
- [ ] Register the distributed profile only together with item inspection, finite physical retention, admission/accounting and recovery endpoints from 7.5.

### Parent scheduling and recovery (7.3)

`DistributedStepScheduler` is an explicit dependency of `AsyncExecutor`; neither hosted startup path supplies it. Eligible asynchronous executions persist `workProfile: distributed-v1`. The parent claim protects stage admission and result publication; independently owned items perform connector calls outside that claim. Triggers and the content-deduplication source continue through the ordinary parent-owned path. A source/output currently creates one whole-step item, so independent pull sources and outputs can overlap, while array-valued outputs still produce a single delivery. YAML fan-out and its concurrency configuration remain 7.4.

Admission persists an immutable context, input manifest, generation and deadline in the parent **before** initializing the journal or publishing a hint. If initialization stops, indexed parent recovery finishes the same manifest. Recovery visits at most 64 admitted stages per parent per pass, with a persisted rotating cursor; the experimental profile caps source/output definitions at 256. Each stage is isolated during recovery, so a corrupt record records a bounded `recoveryIssue` without preventing sibling stages or other executions from progressing.

Item hints include generation and ordinal and use queue envelope version 2; ordinary hints retain version 1. Local queue coalescing includes both item fields. A stale generation or invalid ordinal cannot dispatch a connector, and ordinary executors never reinterpret item hints as regular source/output work. Queue messages contain no pipeline context or credentials.

The runner checks parent authorization at each pre-call/checkpoint boundary, under a short parent claim. It rejects a stopped, expired, deleting or superseded parent, a changed revision, or a different admitted stage. Stopping a parent after a call was dispatched does not establish that the remote write was canceled. Deadline handling reads the item journal and retains that uncertainty in parent history. A completed item/aggregate survives loss of the parent-state save response; recovery publishes its saved result without repeating delivery. Successful results and failed-step diagnostics/attempts are copied into the parent on publication, rather than on every item checkpoint.

Activation remains blocked until 7.5 supplies journal-aware cleanup and operator actions: the ordinary retention loop skips distributed parents, and ordinary resume/replay explicitly refuse them. This prevents deleting recovery evidence or resetting a parent independently of its children. **Do not register this experimental constructor in a consumer installation.** Hosts must also supply consistently scoped connector/secret providers to both ordinary and distributed runners. The existing production profile is unchanged.

Local scheduler verification on 12 October: **281 pipeline tests**, including **16 scheduling regressions**, **14 hosting tests** and **10 queue-codec tests** pass. Both .NET targets build. The new cases cover overlapping sources, duplicate/lost hints, interrupted admission, lost parent saves before/after commit, parent stops, in-flight expiry, authored automatic/manual retries, failed diagnostics, stale generations, corrupt-item isolation and fair recovery across 70 stages. These tests use independent filesystem store instances and controlled connectors; cross-process provider failover and cloud cost measurements remain 7.7. Remote CI evidence will be recorded after verification.

## Verification evidence and reproduction

Local run, 10 October 2026:

- New suite: **23 passed**, using two independent StackExchange.Redis connections to Garnet 2.1.8 on loopback with Lua transaction mode enabled.
- Load case: **3,000 items**, 32 competing tasks, shared stage limit 12, duplicate completion for every item, ordered inspection of all results: **2.59 seconds** in the recorded run. This is a coordination-only local check; no connector calls, durable journal, cross-process workers or cloud network are included.
- Failure cases: lost claim/retry replies, stale checkpoint ordering, expired ownership, acknowledged uncertain-write retry, bounded recovery, missing keys, malformed records/flags, partial batch validation, deadline, cancellation, single aggregation publication and absolute expiry.
- Existing pipeline suite: **227 passed**. Adapter builds for both .NET 8 and .NET 10.
- Real Redis, Redis Cluster and failover campaigns: **not run locally**. Docker/WSL are unavailable on this workstation. CI now runs this suite with a disposable `redis:7.4-alpine` Testcontainers instance; a CI pass has not yet been observed for this change.

With Docker available, run:

```sh
dotnet test dotnet/tests/Elwood.Pipeline.Redis.Tests --filter "Category=Integration"
```

For an explicitly started loopback test server, set `ELWOOD_TEST_WORK_REDIS` to its connection string. Non-loopback endpoints are rejected. Tests use unique namespaces and delete only their exact five keys per stage; they never flush a database. The test server must support atomic Lua, hashes, sorted sets and absolute expirations. A protocol-compatible server run does not replace the real Redis campaign.

The Windows check used a workspace-local .NET tool, without installing a service or changing the system runtime:

```powershell
dotnet tool install garnet-server --version 2.1.8 --tool-path .private/tools/garnet
$testServer = Join-Path (Get-Location) '.private/tools/garnet/garnet-server.exe'
New-Item -ItemType Directory -Path .private/garnet-work-tests -Force | Out-Null
Push-Location .private/garnet-work-tests
try {
    & $testServer --bind 127.0.0.1 --port 16389 --lua --lua-transaction-mode --memory 128m --index 8m
} finally { Pop-Location }
```

In a second terminal at the repository root:

```powershell
$env:ELWOOD_TEST_WORK_REDIS = '127.0.0.1:16389'
dotnet test dotnet/tests/Elwood.Pipeline.Redis.Tests --filter 'Category=Integration'
```

The server is a separate process: use separate terminals for server and tests, and stop it when finished. Garnet flags are documented in [its configuration reference](https://microsoft.github.io/garnet/docs/getting-started/configuration). No cloud resources were created for these checks.
