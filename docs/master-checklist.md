# Elwood delivery checklist and work breakdown

Last updated: 8 October 2026. Scope: the `elwood` backend and sibling `elwood-portal` repositories. This is the master progress checklist; [implementation-plan.md](implementation-plan.md) retains the design rationale and historical verification results.

Check a box only when its stated behavior is implemented and verified. An unchecked item may be underway; **Active** identifies current work. Add evidence and remaining limitations when checking an item. A completed service probe does not complete a runtime adapter or a milestone. No work runs in the background between chat turns unless explicitly scheduled.

## Milestone overview

- [x] M1 — Execution correctness and diagnostics.
- [x] M2 — Drafts, immutable deployments and management security.
- [x] M3 — Explicit real/mock local testing and inspection.
- [x] M4 — Durable local execution, retries, recovery and internal retention.
- [x] M5 — Representative connectors and controlled integration verification.
- [ ] M6 — Native cloud installation and enforced storage lifecycle.
- [ ] Adoption — Eagle SAP and Deldo real-world acceptance tests after M6.

M1–M5 completion describes their implemented scope, not production/cloud readiness. The first and second independent-review follow-ups are part of that baseline. DSL behavior is outside the M6 implementation scope.

## M1 — Execution correctness

- [x] Share script/expression classification and step evaluation; distinguish null results, missing scripts and evaluation errors.
- [x] Persist source/output identity, status, timing, documents and error/warning diagnostics, including successful evaluations with warnings.
- [x] Save terminal state after dependency-resolution and transformation failures.
- [x] Validate supported YAML and load supported script references consistently.
- [x] Align portal engine/examples and display `X-Elwood-Warnings` and playground diagnostics.
- [x] Verify executor, API and portal regressions. Evidence: M1 section in [implementation plan](implementation-plan.md).

## M2 — Authoring, deployment and security

- [x] Separate drafts, validation and explicit deployment; pin executions to immutable revisions.
- [x] Check draft concurrency, route collisions and legacy pipeline adoption.
- [x] Enforce management permissions, pipeline scopes, path containment and scoped secret access.
- [x] Connect portal save/validate/deploy flows and permission-aware controls.
- [x] Verify draft/live separation, old-version execution and management boundaries. Cloud catalog parity remains M6. Evidence: [management and deployment](management-and-deployment.md).

## M3 — Local integration testing

- [x] Execute sync and async definitions locally using the shared synchronous step engine.
- [x] Require explicit whole-run real or mock mode; mocks cannot fall through to real systems.
- [x] Test the exact draft/editor snapshot and generate strict fixture templates.
- [x] Provide runnable public HTTP samples and controlled fixtures.
- [x] Persist/display transformed data, connector request/response details and bounded/redacted inspection.
- [x] Verify API, component and real/mock sample behavior. Evidence: [local testing](local-testing.md) and implementation-plan M3 entries.

## M4 — Durable local processing

- [x] Persist acceptance and scheduling intent; recover missed queue notifications.
- [x] Exclude concurrent attempts; checkpoint source responses and completed destinations.
- [x] Apply authored automatic/manual retry policy, including uncertain writes.
- [x] Resume and replay retained executions with explicit uncertainty acknowledgement and revision pinning.
- [x] Default internal retention to 30 days; protect active work; retry interrupted deletion independently of workers.
- [x] Bound synchronous requests and preserve conservative interrupted-write recovery.
- [x] Verify restart, duplicate delivery, uncertainty, replay and retention behavior. Cloud lease ownership remains M6.

## M5 — Representative integrations

- [x] HTTP Basic and OAuth2 client credentials, including sequential response-dependent calls.
- [x] SQL stored-procedure sources and JSON-payload procedure destinations.
- [x] External RabbitMQ ingress with durable acceptance/reconnect behavior.
- [x] Blob sources/destinations and Event Grid validation/event parsing.
- [x] Controlled order → CRM → Emarsys, RabbitMQ + SQL → HTTP Basic SAP, and Deldo Blob → SQL + Blob examples.
- [x] Close the three M5 verification tasks and both independent-review packages. Evidence: implementation-plan closeout and backend `be7b6c2`, portal `b6af335`.
- [x] Publish integration and adoption documentation. Delivered-output retention is an explicit M6 release gate, not an M5 capability.

## M6 — Work packages and dependencies

Implement and verify Azure first. AWS/GCP support must not be advertised until equivalent native deployment and acceptance work is complete. Selection/scheduling of the next provider is a later product decision; it does not block Azure implementation.

### 6.0 — Approved lab and cost control

- [x] Record temporary `elwood-m6-rg`, West Europe and **€10 total allowance**; ask before exceeding it.
- [x] Provision isolated Standard LRS Blob/Queue/Table storage with Entra authentication and finite lab lifecycle settings.
- [x] Verify security settings, account-scoped roles and authenticated Queue/Table access.
- [x] Prove Blob lease exclusion/renewal/takeover and version/ETag-safe deletion against real Azure.
- [x] Fix version-deletion permissions and clean live test data. Evidence: `3b3b85b`, [lab verification](m6-azure-profile.md#live-lab-verification--8-october-2026); 3 live probes passed, 100 bytes soft-deleted.
- [x] Record the first reported Azure charge: EUR 0.0000201 on 8 October 2026 (billing is delayed; this is not a final campaign total).
- [ ] Continue accounting for retained versions, logs and operations through the campaign, staying within the allowance.
- [ ] Remove the temporary group after the M6 verification campaign and verify deletion.

### 6.1 — Worker ownership and fencing — Complete

Depends on 6.0 for live verification.

- [x] Introduce an execution claim contract that signals lost ownership and preserves the original owner's identity across awaits.
- [x] Bind durable state writes to that claim; a stale worker cannot reuse a replacement owner's lease or publish a checkpoint.
- [x] Check ownership immediately before external calls; propagate lease loss to in-flight connectors.
- [x] Preserve uncertain-write checkpoints on ownership loss; only the next owner classifies recovery.
- [x] Cover sync execution, acceptance, queued work, resume/replay, recovery and retention ownership.
- [x] Implement renewable Azure Blob claims with bounded renewal attempts, conservative loss handling and storage-enforced conditional writes.
- [x] Verify forced/natural loss, late connector completion, concurrent owners and local-runtime compatibility.

### 6.2 — Durable cloud state and indexes — Active

Depends on 6.1.

- [ ] Implement Blob-backed execution state with immutable/checkpoint identity and fenced state changes.
- [ ] Add bounded Table indexes for due work, retained/expired executions and management listing; avoid full retained-history scans on recovery ticks.
- [ ] Recover acceptance/index/state/notification gaps without acknowledging unrecoverable work.
- [ ] Isolate corrupt records per item and expose adapter health; reconcile stale indexes safely.
- [ ] Retain idempotency and pinned revision semantics across workers and restarts.
- [ ] Verify concurrent duplicate acceptance, conditional writes, interrupted transitions, indexed queries and per-record failure handling.

### 6.3 — Execution documents and immutable bundles

Depends on 6.1; coordinates with 6.2 and 6.5.

- [ ] Store execution documents using ownership-safe immutable identities; prevent stale writers from changing referenced data.
- [ ] Implement execution-scoped deletion including versions/snapshots and restartable progress.
- [ ] Recover/clean failed-acceptance and temporary uploads without deleting active writers' data.
- [ ] Keep drafts and referenced immutable definitions separate from expiring execution data.
- [ ] Verify restart, prefix isolation, missing/corrupt documents, stale writes and cleanup.

### 6.4 — Queue transport and recovery scheduling

Depends on 6.1–6.3.

- [ ] Implement Azure Storage Queue transport with bounded messages, visibility/redelivery and poison-message handling.
- [ ] Treat queues as wake-up hints; persisted state remains authoritative.
- [ ] Implement bounded recovery scheduling, deduplication and backoff without one-second full-store scans.
- [ ] Verify lost notifications, duplicate/redelivered messages, worker restarts and delayed retries.
- [ ] Define/verify the workplace Service Bus profile separately; preserve external RabbitMQ as third-party ingress.

### 6.5 — Shared catalog and management parity

Depends on 6.2–6.3.

- [ ] Extract a deployment-store contract from the filesystem implementation.
- [ ] Implement shared drafts, concurrency tokens, immutable bundles and atomic active-route publication.
- [ ] Retain management scopes, authentication, secret grants and per-pipeline history authorization in the hosted runtime.
- [ ] Connect the portal to the hosted management API; save/validate/test/deploy behave consistently with local use.
- [ ] Verify concurrent deployment, collision handling, rollback and executions pinned to earlier versions.

### 6.6 — Native Azure hosting

Depends on 6.1–6.5; review estimated cost against the remaining allowance before deploying compute.

- [ ] Replace the legacy Functions/Redis prototype with .NET isolated native hosting and the durable adapters.
- [ ] Provide repeatable Bicep deployment/configuration with managed identities and appropriately scoped data/secret permissions; no mandatory Redis in the small profile.
- [ ] Host HTTP triggers, management endpoints, queue consumers, recovery and independent retention workers.
- [ ] Keep sync budgets below gateway deadlines and verify real disconnect/cancellation behavior.
- [ ] Deploy Event Grid validation/delivery and verify duplicate events and authorization.
- [ ] Document/provide the always-running ingress option needed for external RabbitMQ; price it separately from scale-to-zero Deldo hosting.
- [ ] Verify deploy, startup/readiness, restart and hosted management/portal parity.

### 6.7 — Mandatory internal and delivered-output retention

Depends on 6.1–6.3 and 6.6. Full contract: [storage-retention.md](storage-retention.md).

- [ ] Validate positive finite configuration: 30-day defaults, pipeline policy and destination override, pinned to the effective revision/write.
- [ ] Persist output ownership and cleanup obligation **before** delivery, independent of execution history and pipeline lifetime.
- [ ] Capture version/ETag identity and expiry in receipts; safely reconcile uncertain/partial writes.
- [ ] Implement independent bounded expiry sweeps with restartable deletion and per-object fault isolation.
- [ ] Cover current/noncurrent versions, snapshots, soft delete, staging/orphans and auxiliary index/control records.
- [ ] Install/verify native lifecycle safeguards without deleting active recovery data or unrelated/source-owned objects.
- [ ] Detect permission/policy drift, immutability failures and overdue cleanup; expose failed health and actionable diagnostics.
- [ ] Enforce finite active execution/retry lifetime, accepted bytes and backlog/admission limits.
- [ ] Verify default/override expiry with advanced clocks and actual provider deletion; outputs outliving history, replay, replacement, source isolation and interrupted cleanup.

### 6.8 — Operations, release verification and handoff

Depends on the preceding implementation packages.

- [ ] Add cloud adapter contracts and repeatable opt-in live verification to CI/test instructions; clearly separate emulator coverage from Azure evidence.
- [ ] Run all relevant backend/API/portal suites and review remaining dependency advisories before external hosting.
- [ ] Reproduce the three controlled integration families through the hosted installation, including restart/recovery and retention.
- [ ] Measure transactions, retained bytes, compute and monitoring costs; compare the small profile with the pet-project budget.
- [ ] Document installation, identity/secrets, quotas/health, backup policy, upgrades, migration, rollback, recovery and teardown.
- [ ] Independently review the completed M6 changes and resolve release-blocking findings.
- [ ] Check M6 complete only after hosted behavior, security, recovery and retention evidence is recorded.

## Adoption after M6

- [ ] Prepare a versioned, tested handoff for the Eagle SAP integration session.
- [ ] Prepare a versioned, tested handoff for the Deldo integration session; resolve its hosting/event-delivery decisions using the implemented profile.
- [ ] Run both real-world acceptance exercises with their project owners and fix defects discovered there.
- [ ] Decide the next native provider and public release/support claims based on verified behavior.

## Progress record

| Date | Package | Evidence / result |
|---|---|---|
| 8 October 2026 | Baseline | M1–M5 and review follow-ups recorded in implementation-plan.md; no new rerun claimed here. |
| 8 October 2026 | 6.0 | Temporary storage lab deployed; 3 live Blob tests passed; `3b3b85b`. Actual billing query still had no rows. |
| 8 October 2026 | Checklist | Master WBS created; 6.1 is the next implementation package. |

| 8 October 2026 | 6.1 | Fenced claim contract, runtime ownership-loss handling and renewable Blob claims. 202 pipeline tests passed before the final additional case; all 6 ownership cases then passed. Three simulated renewal-failure tests, 2 live Azure claim tests and 5 isolated API contracts passed. Cloud state/index/document integration remains 6.2–6.3. |
