# Item inspection and recovery — M7.5 design

Status: **implementation in progress, 6 November 2026**. Read/selected-retry APIs, explicit distributed replay, journal status counts, portal controls, bounded physical cleanup and Azure admission/late-upload accounting are implemented for an explicitly configured journal/scheduler. They are **not registered for production use** in API/Functions yet. Complete installed-host quota/health/retention/cold-Redis verification and registration remain required before enabling the distributed profile. The original design was agreed on 12 October; dated packages below record their respective baselines.

## Operator workflow

An execution remains attached to its pipeline revision. Its step overview shows item counts, active attempts, completed items, failed items, items awaiting review and the time at which those counts were observed. Selecting a source or output opens a paginated item table. Rows identify the stable, zero-based manifest ordinal; optional business labels come from inspected data, never from an identity used to authorize a retry.

The table supports status filtering and ordinal lookup. A detail drawer shows the selected input, mapped output, diagnostics (including successful warnings), attempt history, call details, retained receipts and completed destinations. Payloads are fetched on demand, bounded and redacted through the existing inspection contract. The first page must not download every item or every document. A missing cache record is shown as unknown until the journal is consulted; it is never presented as pending work.

Operators can select current failed/review items and choose **Retry selected items**. The confirmation lists the exact items, their current attempt numbers and the pinned revision. If any outcome is uncertain, a separate acknowledgement explains which external writes may already have happened and may be repeated. A generic confirmation checkbox is insufficient. Completed items cannot be selected or reset. Items with delivered destinations retain their receipts and skip those destinations during the next attempt.

Ordinary retries continue the same execution and original revision. A replay is a separate execution with explicit whole-execution scope and duplicate-write acknowledgement; it does not reset the original journal. Revision changes belong to replay, never a retry. The 6 November package below defines the replay options and installed-host capability gate.

## Management contract

The following routes are implemented in the shared HTTP dispatcher. They authorize the parent execution before journal access. A host without a journal returns 409; retry additionally requires explicit `WorkItemRecovery` registration so it cannot accept a command that its recovery worker cannot reconcile. Existing API/Functions deployments continue using their current execution profile.

| Route | Contract |
|---|---|
| `GET /api/executions/{id}/work-stages` | Bounded stage summaries, pinned revision, generation, deadline, retention expiry, observation timestamp and recovery/retention health. |
| `GET /api/executions/{id}/work-stages/{stage}/items?status=…&cursor=…&limit=…` | Default 50, maximum 100 items; opaque cursor binds execution, stage generation, filter and position. Return a continuation even when a bounded scan finds no matching rows. |
| `GET /api/executions/{id}/work-stages/{stage}/items/{ordinal}` | Current authoritative item, retained attempt references, outcome uncertainty, completed destinations and allowed actions. Separate cursor for attempt history. |
| `GET /api/executions/{id}/work-stages/{stage}/items/{ordinal}/inspection` | Bounded previews and diagnostics for an identified attempt; no arbitrary caller-supplied storage references. |
| `POST /api/executions/{id}/work-stages/{stage}/retry` | At most 100 explicit ordinals, each with expected attempt, generation and uncertainty acknowledgement, plus a stable request ID. Return per-item accepted/conflict/rejected results. |

Read and recovery permissions remain distinct and pipeline scoped. Resolve the parent before dereferencing child records; unauthorized and unknown IDs return the same 404 behavior. Validate cursor size/shape and all storage identities. Never trust cached eligibility for an operator mutation.

A retry command is durable before queue publication. Repeating its request ID after a lost HTTP reply returns the original decision; it cannot create a second attempt. Reject changed payloads under an existing request ID. A concurrent automatic retry, already running item, mismatched generation/attempt, expired execution, deletion or superseded replay is a conflict. Stage limits still apply across every worker. Partial batch acceptance is explicit; the UI retains rejected selections and refreshes their reasons.

The parent claim coordinates reopening a failed/review execution and recording the recovery command. It must not authorize an already completed destination again. Child retries use the journal's fenced expected-attempt contract. Reconcile an interrupted batch from persisted decisions before declaring it accepted or finished; queue messages remain replaceable hints. Tests must interrupt recovery before and after each durable boundary.

Counts currently use bounded journal scans persisted in the parent, carrying their observation interval and coverage. They require no Redis entries and no full scan within an HTTP request. Actions always recheck durable state; counts never authorize recovery. Poll while work or a recovery request is active, and stop for terminal executions; manual refresh remains available.

## Retention and admission are part of the same gate

Default history retention remains 30 days, configurable per pipeline; delivered output blobs retain their destination overrides. Inventory and delete item manifests, stage headers, item/permit records, input/context documents, checkpoint histories, aggregate pages, abandoned uploads, command receipts and Redis projections. Existing pipeline/source data is not owned by this cleanup.

Terminal retention and the absolute execution deadline must have documented precedence. Active owners and a recovery command cannot race deletion. A deletion intent prevents new claims/retries, then drains or fences existing owners before removing documents and journals in bounded resumable pages. Retain delivery cleanup obligations until the output is deleted or its separately recorded obligation is safely transferred. Crashes, partial deletion and missing/corrupt records must not abandon an unbounded storage namespace.

Reserve finite item, document-byte and attempt capacity before accepting work. Reject unavailable capacity with a useful diagnostic and retry guidance; never durably accept an unbounded manifest. Account for orphaned partial admission, result pages, repeated checkpoints and replay. Retention health must be checked before admission and remain observable independently of ordinary work. Physical cleanup is required even when Redis TTLs have expired.

## Implementation and verification checklist

- [x] Define typed API DTOs, cursor encoding, authorization checks and journal-backed paged inspection.
- [x] Persist idempotent retry commands; integrate parent reopening, child eligibility and interrupted-command recovery.
- [x] Build the portal item table, detail drawer, filters and separate uncertain-write confirmation.
- [x] Implement parent/stage deletion intents and bounded, restartable journal/document/version/projection cleanup; preserve independent output obligations.
- [x] Implement Azure quota reservations and retention health data, including journal writes, late partial uploads and reserved cleanup-checkpoint capacity; production binding remains an installed-host gate.
- [x] Verify selected retries leave successful items/destinations unchanged; include concurrent retries and lost responses.
- [ ] Complete hostile-input/installed-host verification. Parent-scope denials, malformed/filter-mismatched cursors, stale attempts and expired/deleting parents have regression coverage; no endpoint accepts a caller-supplied document reference.
- [x] Verify successful warnings, failed maps, corrupt items and large histories without unbounded API responses.
- [x] Verify active claim drain, interrupted cleanup, restart, scoped document cleanup and preserved output-blob obligations with local regression tests.
- [x] Verify finite stage/attempt reservations, quota rejection before uploads, late journal completion after parent deletion and cleanup progress at full quota using Azure SDK transport regressions.
- [ ] Complete provider and installed-host accounting/health/cold-Redis verification before activation; keep the filesystem test adapter outside supported distributed installation profiles.
- [ ] Register the distributed host profile only when the above gates pass; retain existing profile behavior and install-time opt-in.

The first portal implementation can use these code-oriented controls. A graphical pipeline designer is outside this milestone. Cross-process throughput, managed Redis failover and measured hosting costs remain M7.7; consumer infrastructure and versioned tutorials remain M7.8.

## First implementation package — 3 November 2026

Item pages scan at most the requested 1–100 ordinals. Filtering is applied to that bounded scan, so an empty result can still have `nextCursor`. Cursors bind the execution, stage, generation and filter; attempt cursors additionally bind the observed item version. They are continuation positions, not authorization capabilities. The API always checks parent permissions independently.

Stage summaries currently report manifest size, aggregated prefix, revision, deadline/expiry and recovery issue. They do **not** claim current whole-stage status counts. Item pages include observation time and scanned count. Redis-backed status summaries and retention/capacity health are still part of the remaining gate.

Item details return at most 20 attempts (default 10), 64 diagnostics per record and 16 connector calls per attempt, with a 192 KiB attempt-page budget. Oversized call captures are explicitly marked omitted. Checkpoints over 4 MiB are not materialized by preview-capable stores: the response identifies the retained attempt count and `historyOmitted`, rather than claiming there were no calls. Input/output/receipt previews share a 256 KiB budget, 64 KiB per document. Credential headers/query fields use the existing captured-call redaction; payload bodies remain the original business data under pipeline-scoped read permission.

The first selected-retry implementation accepts new commands only after the parent has settled as Failed/AwaitingReview. This avoids pausing unrelated running items while a recovery command owns the parent. A repeat of an existing request ID can still retrieve its decision after execution resumes. Retrying failures while siblings are actively running is not offered.

Retry request example:

```json
{
  "requestId": "operator-generated-stable-id",
  "generation": "stage-generation-from-inspection",
  "items": [
    { "ordinal": 12, "expectedAttempt": 2, "acknowledgeUncertain": true }
  ]
}
```

The parent claim first saves `PendingWorkRetry` and moves the execution to the recoverable index. Each accepted child transition retains its checkpoint, receipts and completed destinations. Decisions are saved incrementally; a transport fault leaves the command pending. The scheduler reconciles that command before dispatch. A durable receipt under `{execution}/recovery/{requestId}` records the final per-item decisions; repeating an ID returns those decisions, while a changed payload conflicts. Commands are limited to 100 explicit items and 1,000 commands per execution; these bounds do **not** replace the still-required storage quota/physical retention implementation.

An expanded stage now waits for its admitted sibling work to settle before publishing failure. This lets a selected retry finish while other items retain their terminal failures. Ordinary retry keeps the original execution and revision. Distributed replay remains unavailable and the portal explains this instead of offering the legacy whole-execution actions.

Regression evidence covers lost child/receipt replies, interrupted-command recovery, concurrent competing commands, uncertainty acknowledgements, stale attempts, successful warnings, corrupt/oversized history, HTTP authorization/paging and a real item-executor retry preserving sibling deliveries and an already delivered destination. The portal tests cover selection eligibility, separate acknowledgement, a stable request after a lost reply, empty-page continuation, lazy inspection and stopping terminal polling. No cloud resources or pilot integrations were deployed for this package.

Local verification: **316 Pipeline tests**, **15 Hosting tests**, and **30 focused journal/recovery regressions** after the final corruption/health-reporting adjustment. The Pipeline package builds for .NET 8 and .NET 10. Portal: **120 Node tests**, **21 component tests**, TypeScript and the standard production build pass; lint has no errors and its three pre-existing dependency warnings. Compatible Next.js/DOMPurify/follow-redirects patches remove the reported production advisories (`npm audit --omit=dev`: zero). The full development dependency audit still has 16 moderate/two high findings. No new live Azure/Redis throughput or browser walkthrough is claimed; CI results are separate from these local checks.

## Bounded physical cleanup package — 5 November 2026

`WorkExecutionRetention` advances one bounded unit per expired parent per maintenance tick. The parent claim first saves `Deleting`; execution, selected retry and replay no longer authorize new work. Each stage then saves a deletion tombstone under its header lease. Item acquisition rechecks that tombstone after acquiring its permit/item claims, and existing item checkpoints validate it too. Cleanup never breaks a live claim: it waits for the owner to release or lose its storage lease. An in-flight connector can therefore delay cleanup; the execution deadline still bounds the owner's lifetime.

The sequence is stage drain, bounded Redis projection deletion when configured, internal document deletion, then journal/header/retry-receipt deletion and finally parent deletion. Progress lives in the parent checkpoint, outside the namespace being swept. The terminal parent's retention expiry takes precedence over the stage's absolute deadline-plus-retention backstop. Default retention remains 30 days; this package adds no YAML or DSL semantics.

File and Azure journal adapters implement pages of at most 256 entries (128 by default). Cleanup uses scoped inventory rather than parsing item payloads, so truncated item/manifest records do not prevent garbage collection. A missing/corrupt admitted stage header still fails closed and reports a cleanup failure; another parent can continue. An interrupted admission that has not created a header is handled separately. The local distributed document store explicitly requires a fresh execution-directory layout; it refuses to reinterpret an existing flattened store, whose ambiguous underscore paths cannot safely support scoped cleanup.

Azure pages include current blobs, versions and snapshots. Deleting a current blob can expose a previous version behind the continuation; completion therefore requires an entire pass without deletions or busy records. Cleanup cursor updates are saved in the parent, not the stage header, so one-record pages cannot indefinitely generate and chase new header versions. Lost page responses are safe to repeat. Redis cleanup deletes bounded, exact generation-qualified keys and tolerates already missing entries; it does not scan a whole Redis database. A compact generation-closing marker blocks delayed projection writes and expires at the original absolute stage expiry. Cache loss/failover remains part of M7.7 verification; Redis never grants durable work permission.

Internal document cleanup expires only `document`/`checkpoint` obligations. Delivered business blobs and their destination-specific expiry remain in the independent output ledger, including after the parent and internal receipts are removed. The Azure regression exercises this separation explicitly.

**Activation gates remain:** installation-wide item/document/attempt reservations before admission, independently tracked journal/partial uploads from lost owners, reserved capacity for every cleanup checkpoint, admission pressure/health, and deterministic late-admission/cleanup race verification. The new coordinator is injectable for tests but is not registered in either production host. Existing Azure document obligations already cover unconfirmed document uploads; that does not establish equivalent journal accounting. M7.5d/e and the installed-host gate must be closed together before claiming a bounded distributed installation.

The activation-gate paragraph above records the physical-cleanup package's baseline. The following package implements its accounting components; full host activation remains open.

Local verification: the solution builds without warnings/errors, including the .NET 8 package targets. The full non-integration run passes **327 Pipeline, 48 Azure adapter, 15 Hosting, 374 Core, 22 CLI, 21 connector, seven Parquet and two Redis transport tests**. After adding the final interrupted parent-progress save regression, all **12 focused retention tests** pass, and the Redis adapter/runtime rebuild passes. All **five API contract tests** pass against that freshly built DLL, selected explicitly through `ELWOOD_TEST_RUNTIME_DLL`. Real Redis and Azurite lease/pagination regressions are included in the existing Docker CI jobs; no new live Azure deployment or capacity certification is claimed here.

## Quota and late-upload accounting package — 5 November 2026

`IWorkCapacity` reserves an immutable stage request before the scheduler uploads its context, item inputs or manifest. The request binds the parent, stage, revision, input hash, item count, concurrency and deadlines. Repeating it returns the same generation; changing it conflicts. The Azure ledger stores stage reservations in the same quota partition as the parent, so closing the parent also rejects a previously unseen stage from a delayed worker. A missing parent reservation cannot create work. This state is independent of Redis and survives a replacement process.

The adapter defaults are 100,000 reserved items and 1,000,000 reserved attempts per ledger partition, with 100 attempts reserved per item unless the adapter is explicitly configured lower. Thus the attempt allowance normally limits a partition to 10,000 items before other limits apply. These are finite protective ceilings, **not certified capacity or new YAML settings**. Each attempt consumes allowance before committing Running or dispatching a connector. An uncertain reservation consumes allowance conservatively; it is not refunded from an assumed failure. Installation settings and the measured Eagle envelope remain 7.5g/7.7/7.8.

Documents and every journal create/update reserve actual UTF-8 bytes and one physical object/version before upload. Existing object, byte and hourly limits still apply; partial admission cannot bypass them. A journal upload registers an independent cleanup identity first, stamps that identity into Blob metadata and then records the acknowledged ETag/version. Unacknowledged uploads keep a one-day obligation. Resuming a known create confirms the original obligation instead of treating a legitimate Pending item as an orphan. Confirmed journal obligations use the stage's finite expiry. Cleanup sets a one-way flag: a late successful upload cannot extend an already expired obligation. A stage creation also checks its original generation, including after quota reservation and before dispatching the storage request.

The independent sweep preserves current journal records and stage-header reservations while the parent reservation exists. Parent pruning owns their drain sequence. Once parent cleanup removes its namespace, independent obligations still find late uploads and their versions. Closed stage reservations remain until cleanup is complete and the original admission deadline plus a one-day grace has elapsed; this prevents stale admission from borrowing a new allowance. Stage cleanup metadata and reservation release also bind the generation: a delayed sweep cannot delete a later generation's header or release its allowance when an external execution ID is reused. Delivered output blobs keep their separate destination retention and are unaffected by internal accounting.

Each stage prepays two 1 MiB header slots for the one-way deletion transition. Each execution's existing three 4 MiB reserved slots cover terminal state and a rotating pair of cleanup checkpoints. Azure previously ignored every save after `Deleting`; it now persists only cleanup progress during that phase. Each successor stores the exact prior reserved version ID and removes the older version before writing again. This avoids an inventory scan on every page and keeps at most two reserved cleanup versions, including after a lost save reply. Lease and ETag conditions prevent an SDK retry from appending another version for the same write. Ordinary quota exhaustion therefore does not block deletion markers or restartable cleanup progress.

Retention health includes reserved items/attempts, used attempts and available objects/bytes/item/attempt allowances. Partitioned availability reports the lowest partition allowance rather than implying that a hot partition can borrow unused space elsewhere. New stage and attempt admission invoke the configured retention-health guard; cleanup remains available when that guard rejects ordinary work. Production registration must bind the initialized ledger, native lifecycle/readiness guard, capacity service, accounted journal/documents and retention coordinator together. The optional unaccounted constructors are retained for isolated component tests; they do not constitute a supported distributed installation. No host profile was enabled by this package.

Regressions cover concurrent/idempotent and conflicting admission, cross-partition parent closure, restart, attempt exhaustion without another Running transition, rejection before context/input uploads, UTF-8 byte/object limits, lost and late upload replies, resumed admission confirmation, stale generations, independent sweeps racing parent cleanup, and lost cleanup-save replies at full quota. A real Azurite test exercises two independent ledger clients and the reserved header path; versioned-storage behavior uses the deterministic Azure SDK HTTP transport because Azurite does not implement Blob versioning. Installed-host/cold-Redis acceptance and provider load/failover measurement remain open.

Local verification: the full solution builds with zero warnings/errors, including .NET 8 package targets. **330 Pipeline, 61 Azure adapter, 15 Hosting and two Redis transport tests** pass. After the final delayed-sweep generation regression, all **18 focused accounting/retention tests** pass and the runtime/.NET 8 Azure package rebuild passes. All **five API contracts** pass against the rebuilt DLL with `ELWOOD_TEST_RUNTIME_DLL` and `--test-concurrency=1`. An initial parallel contract run, competing with the .NET suites, exhausted the two-second sync budget before the fixture's first upstream call; the isolated rerun passes without changing its assertions. Portal code is unchanged. Real Azurite/Redis verification runs in CI, not against a new live Azure lab.

## Distributed replay and stage counts — 6 November 2026

Replay accepts only `whole-execution` scope. It creates a new durable execution from the retained original trigger payload/metadata, using the original snapshot by default or an explicitly selected published revision of the same pipeline. The selected revision must remain asynchronous and retain the trigger source. Sources run again, so external pull data can differ. Item subsets use **Retry selected items**, which preserves successful deliveries and the original revision; replay creates fresh manifests, generations, checkpoints and delivery receipts without resetting the original journal.

`POST /api/executions/{id}/replay` retains the existing `revisionId`, `acknowledgeUncertain` and `forceProcessing` fields. Distributed executions additionally require:

```json
{
  "requestId": "operator-generated-stable-id",
  "scope": "whole-execution",
  "acknowledgeDuplicates": true,
  "acknowledgeUncertain": true,
  "forceProcessing": false
}
```

The portal requires separate confirmations for whole-pipeline scope, repeated completed deliveries, and uncertain writes. Both write acknowledgements are mandatory for distributed replay, even if the parent currently reports no uncertainty: an execution deadline may settle the parent before every in-flight child reports its outcome. File-content suppression still applies unless `forceProcessing` is explicitly selected. A forced replay fences further recovery of the original, as in the existing ordinary profile.

The original parent claim serializes replay with selected retry and deletion. Before child creation it records the request hash and a reserved new execution ID. A repeated request reconciles that same child after an interrupted or lost response; changed options conflict. An accepted receipt returns the original child ID even after that child's retained history is removed, and never recreates it. The new execution goes through normal acceptance, capacity and retention independently. There is no partial journal migration or sharing of delivery receipts.

At most 100 replay commands are retained per original execution. An unaccepted command can create its reserved child for at most ten minutes, shortened to half the selected revision's retention when that is smaller; after that, it may reconcile an existing child but cannot recreate an absent one. This bounds an ambiguous creation window after child history has expired. Another command and selected-item retry wait until the outstanding creation window ends or the original request is reconciled. Resend the original request after a lost response; if the window expired without a retained child, inspect execution history before intentionally submitting a new request ID. The portal retains the request and locks its options while resending. Before HTTP 202 there is no promise of automatic creation by a recovery worker: the caller must resend. Once accepted, the ordinary durable recovery index processes the child even if queue publication was lost.

`GET /api/executions/{id}/recovery-options` checks parent-scoped read permission and reports whether replay is supported by the installed profile. Replay itself checks recovery permission and refuses distributed execution unless the host explicitly supplies `DistributedExecutionFactory`. API and Functions use the same dispatcher. Contract fixtures exercise both absent and explicit registrations; this hook does not activate the profile in either shipped host. Complete quota/health/retention wiring and cold-Redis verification remain 7.5g.

Stage summaries now include counts for Pending, Running, Completed, Failed, AwaitingReview, Retrying and Unknown, plus `scanned`, `complete`, `startedAt` and `observedAt`. The scheduler derives them from its existing shared 64-item recovery budget; management reads do not scan every journal item. Partial scans persist across process replacement. Unreadable/corrupt items count as Unknown, never Pending. A completed scan remains visible with its observation interval while the next scan advances; initial coverage is explicitly partial. Ordered aggregation can publish all-completed counts only after verifying every immutable completed result. Selected retry clears the previous summary before starting a new scan.

These are durable journal observations over an interval, not an atomic current snapshot. Counts do not authorize actions; selected retries still recheck the item and its attempt. Redis eviction has no effect on these summaries. Terminal polling stops, manual refresh remains available, and older executions without observations display counts as unavailable rather than zero. The portal's item-page counts remain separate from the stage summary.

Regression coverage includes original/selected revision replay, unchanged original item records, new generations, both acknowledgements, forbidden scope, changed request options, interruptions before/after intent/child/receipt saves, retained receipt behavior after child deletion, creation-window expiry, parent eligibility and the forced-processing fence. Summary coverage includes a 150-item scan across replacement schedulers and an unreadable final record. Shared HTTP tests cover authorization, disabled installations and stable replay in an explicitly configured fixture. No cloud resources, live installation or throughput certification are claimed by this package.

Local verification: the full solution builds with zero warnings/errors, including .NET 8 and .NET 10 packages. The full Pipeline suite passes **343 tests** before the final eligibility/short-retention cases; the final **19 focused replay/count regressions** pass after those additions. **62 Azure adapter tests**, **16 Hosting tests** and all **five HTTP contracts** pass. Contracts use the rebuilt DLL through `ELWOOD_TEST_RUNTIME_DLL` and run serially. Portal `05bda09`: **120 Node tests**, **24 component tests**, production build/TypeScript and lint pass (three pre-existing hook dependency warnings, no lint errors); [portal CI](https://github.com/max-favilli/elwood-portal/actions/runs/42189453072) is green. Provider integration and final whole-suite totals are verified separately by backend CI.
