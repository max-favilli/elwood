# Item inspection and recovery — M7.5 design

Status: **implementation in progress, 5 November 2026**. The read/selected-retry API, portal controls and bounded physical-cleanup coordinator below are implemented for an explicitly configured journal/scheduler. They are **not registered for production use** in API/Functions yet. Admission quotas and late-upload accounting, distributed replay and installed-host verification remain required before enabling the distributed profile. The original design was agreed on 12 October; the current implementation and limits are recorded below.

## Operator workflow

An execution remains attached to its pipeline revision. Its step overview shows item counts, active attempts, completed items, failed items, items awaiting review and the time at which those counts were observed. Selecting a source or output opens a paginated item table. Rows identify the stable, zero-based manifest ordinal; optional business labels come from inspected data, never from an identity used to authorize a retry.

The table supports status filtering and ordinal lookup. A detail drawer shows the selected input, mapped output, diagnostics (including successful warnings), attempt history, call details, retained receipts and completed destinations. Payloads are fetched on demand, bounded and redacted through the existing inspection contract. The first page must not download every item or every document. A missing cache record is shown as unknown until the journal is consulted; it is never presented as pending work.

Operators can select current failed/review items and choose **Retry selected items**. The confirmation lists the exact items, their current attempt numbers and the pinned revision. If any outcome is uncertain, a separate acknowledgement explains which external writes may already have happened and may be repeated. A generic confirmation checkbox is insufficient. Completed items cannot be selected or reset. Items with delivered destinations retain their receipts and skip those destinations during the next attempt.

Ordinary retries continue the same execution and original revision. A replay is a separate execution with explicit scope and duplicate-write acknowledgement; it does not reset the original journal. Revision changes belong to replay, never a retry. M7.5 must define the user-facing replay options alongside the existing whole-execution replay controls before connecting those controls to distributed executions.

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

Counts and filtered lists may use Redis projections, carrying their observation time. Actions always recheck durable state. Cold, evicted or stale projections use bounded journal scans and reconciliation; they must not force a full scan within one HTTP request. Poll while work or a recovery request is active, and stop for terminal executions; manual refresh remains available.

## Retention and admission are part of the same gate

Default history retention remains 30 days, configurable per pipeline; delivered output blobs retain their destination overrides. Inventory and delete item manifests, stage headers, item/permit records, input/context documents, checkpoint histories, aggregate pages, abandoned uploads, command receipts and Redis projections. Existing pipeline/source data is not owned by this cleanup.

Terminal retention and the absolute execution deadline must have documented precedence. Active owners and a recovery command cannot race deletion. A deletion intent prevents new claims/retries, then drains or fences existing owners before removing documents and journals in bounded resumable pages. Retain delivery cleanup obligations until the output is deleted or its separately recorded obligation is safely transferred. Crashes, partial deletion and missing/corrupt records must not abandon an unbounded storage namespace.

Reserve finite item, document-byte and attempt capacity before accepting work. Reject unavailable capacity with a useful diagnostic and retry guidance; never durably accept an unbounded manifest. Account for orphaned partial admission, result pages, repeated checkpoints and replay. Retention health must be checked before admission and remain observable independently of ordinary work. Physical cleanup is required even when Redis TTLs have expired.

## Implementation and verification checklist

- [x] Define typed API DTOs, cursor encoding, authorization checks and journal-backed paged inspection.
- [x] Persist idempotent retry commands; integrate parent reopening, child eligibility and interrupted-command recovery.
- [x] Build the portal item table, detail drawer, filters and separate uncertain-write confirmation.
- [x] Implement parent/stage deletion intents and bounded, restartable journal/document/version/projection cleanup; preserve independent output obligations.
- [ ] Integrate quota reservation and retention health, including journal writes, late partial uploads and reserved cleanup-checkpoint capacity.
- [x] Verify selected retries leave successful items/destinations unchanged; include concurrent retries and lost responses.
- [ ] Complete hostile-input/installed-host verification. Parent-scope denials, malformed/filter-mismatched cursors, stale attempts and expired/deleting parents have regression coverage; no endpoint accepts a caller-supplied document reference.
- [x] Verify successful warnings, failed maps, corrupt items and large histories without unbounded API responses.
- [x] Verify active claim drain, interrupted cleanup, restart, scoped document cleanup and preserved output-blob obligations with local regression tests.
- [ ] Verify installation-wide accounting and late admission/upload races after ownership loss; complete provider and installed-host verification before activation.
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

Local verification: the solution builds without warnings/errors, including the .NET 8 package targets. The full non-integration run passes **327 Pipeline, 48 Azure adapter, 15 Hosting, 374 Core, 22 CLI, 21 connector, seven Parquet and two Redis transport tests**. After adding the final interrupted parent-progress save regression, all **12 focused retention tests** pass, and the Redis adapter/runtime rebuild passes. All **five API contract tests** pass against that freshly built DLL, selected explicitly through `ELWOOD_TEST_RUNTIME_DLL`. Real Redis and Azurite lease/pagination regressions are included in the existing Docker CI jobs; no new live Azure deployment or capacity certification is claimed here.
