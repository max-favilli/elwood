# Item inspection and recovery — M7.5 design

Status: proposed implementation contract, 12 October 2026. The owner asked to design and plan these controls while M7.4 is implemented. The endpoints and portal screens described here are **not implemented**. They are required, together with finite physical retention and admission limits, before enabling the distributed profile in API/Functions hosts.

## Operator workflow

An execution remains attached to its pipeline revision. Its step overview shows item counts, active attempts, completed items, failed items, items awaiting review and the time at which those counts were observed. Selecting a source or output opens a paginated item table. Rows identify the stable, zero-based manifest ordinal; optional business labels come from inspected data, never from an identity used to authorize a retry.

The table supports status filtering and ordinal lookup. A detail drawer shows the selected input, mapped output, diagnostics (including successful warnings), attempt history, call details, retained receipts and completed destinations. Payloads are fetched on demand, bounded and redacted through the existing inspection contract. The first page must not download every item or every document. A missing cache record is shown as unknown until the journal is consulted; it is never presented as pending work.

Operators can select current failed/review items and choose **Retry selected items**. The confirmation lists the exact items, their current attempt numbers and the pinned revision. If any outcome is uncertain, a separate acknowledgement explains which external writes may already have happened and may be repeated. A generic confirmation checkbox is insufficient. Completed items cannot be selected or reset. Items with delivered destinations retain their receipts and skip those destinations during the next attempt.

Ordinary retries continue the same execution and original revision. A replay is a separate execution with explicit scope and duplicate-write acknowledgement; it does not reset the original journal. Revision changes belong to replay, never a retry. M7.5 must define the user-facing replay options alongside the existing whole-execution replay controls before connecting those controls to distributed executions.

## Management contract to implement

The following are proposed routes. Final request/response schemas and API contract tests belong in the M7.5 implementation commit.

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

- [ ] Define typed API DTOs, cursor encoding, authorization checks and journal-backed paged inspection.
- [ ] Persist idempotent retry commands; integrate parent reopening, child eligibility and interrupted-command recovery.
- [ ] Build the portal item table, detail drawer, filters and separate uncertain-write confirmation.
- [ ] Implement quota reservation, deletion intents, bounded journal/document cleanup and retention health.
- [ ] Verify selected retries leave successful items/destinations unchanged; include concurrent retries and lost responses.
- [ ] Verify denied reads/retries, forged cursors/references, stale generations and expired/deleting parents.
- [ ] Verify successful warnings, failed maps, corrupt items and large histories without unbounded API responses.
- [ ] Verify cleanup during active attempts/recovery, interruption and restart, cold Redis, and output-blob obligations.
- [ ] Register the distributed host profile only when the above gates pass; retain existing profile behavior and install-time opt-in.

The first portal implementation can use these code-oriented controls. A graphical pipeline designer is outside this milestone. Cross-process throughput, managed Redis failover and measured hosting costs remain M7.7; consumer infrastructure and versioned tutorials remain M7.8.
