# Elwood delivery checklist and work breakdown

Last updated: 14 October 2026. Scope: the `elwood` backend and sibling `elwood-portal` repositories. This is the master progress checklist; [implementation-plan.md](implementation-plan.md) retains the design rationale and historical verification results.

Check a box only when its stated behavior is implemented and verified. An unchecked item may be underway; **Active** identifies current work. Add evidence and remaining limitations when checking an item. A completed service probe does not complete a runtime adapter or a milestone. No work runs in the background between chat turns unless explicitly scheduled.

## Milestone overview

- [x] M1 — Execution correctness and diagnostics.
- [x] M2 — Drafts, immutable deployments and management security.
- [x] M3 — Explicit real/mock local testing and inspection.
- [x] M4 — Durable local execution, retries, recovery and internal retention.
- [x] M5 — Representative connectors and controlled integration verification.
- [x] M6 — Code, installer and `v0.7.26` release verified. Complete-day telemetry/cap decision recorded; temporary group deletion confirmed 13 October at 11:59:42 UTC. Latest reported campaign cost **€0.1348 as of 14 October 11:46:41 UTC**, subject to billing delay. Billing-only reconciliation and consumer acceptance of observed heartbeat gaps remain explicit follow-ups. [Campaign and installer evidence](m6-release-evidence.md).
- [ ] M7 — **Active, resumed 12 October at the owner's request.** Durable journals, coordination, parent scheduling and explicit fan-out/aggregation (7.4) pass local checks and all CI jobs. Next: implement the [inspection/recovery design](item-inspection-recovery-design.md), retention and quotas (7.5). Host activation and consumer release remain open. [Contract and work breakdown](distributed-work-items.md).
- [ ] Adoption — Eagle SAP and Deldo consumer installation and real-world acceptance; Eagle's concurrent work-item flow also depends on M7.

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
- [x] Account for retained versions, operations and compute through the campaign: 230.23 MiB listed Blob records, 31,177 storage transactions and 358.3 GB-seconds in the recorded snapshot; delayed billing is distinguished from estimated cost. [Evidence](m6-release-evidence.md#test-totals-and-measured-usage).
- [x] Remove temporary `elwood-m6-rg` after verification; Azure `group exists` returned `false` on 8 October 2026.

### 6.1 — Worker ownership and fencing — Complete

Depends on 6.0 for live verification.

- [x] Introduce an execution claim contract that signals lost ownership and preserves the original owner's identity across awaits.
- [x] Bind durable state writes to that claim; a stale worker cannot reuse a replacement owner's lease or publish a checkpoint.
- [x] Check ownership immediately before external calls; propagate lease loss to in-flight connectors.
- [x] Preserve uncertain-write checkpoints on ownership loss; only the next owner classifies recovery.
- [x] Cover sync execution, acceptance, queued work, resume/replay, recovery and retention ownership.
- [x] Implement renewable Azure Blob claims with bounded renewal attempts, conservative loss handling and storage-enforced conditional writes.
- [x] Verify forced/natural loss, late connector completion, concurrent owners and local-runtime compatibility.

### 6.2 — Durable cloud state and indexes — Complete

Depends on 6.1.

- [x] Implement Blob-backed execution state with immutable/checkpoint identity and fenced state changes.
- [x] Add bounded Table indexes for due work, retained/expired executions and management listing; avoid full retained-history scans on recovery ticks.
- [x] Recover acceptance/index/state/notification gaps without acknowledging unrecoverable work.
- [x] Isolate corrupt records per item and expose adapter health; reconcile stale indexes safely.
- [x] Retain idempotency and pinned revision semantics across workers and restarts.
- [x] Verify concurrent duplicate acceptance, conditional writes, interrupted transitions, indexed queries and per-record failure handling.

### 6.3 — Execution documents and immutable bundles

Depends on 6.1; coordinates with 6.2 and 6.5.

- [x] Store execution documents using ownership-safe immutable identities; prevent stale writers from changing referenced data.
- [x] Implement execution-scoped deletion including versions/snapshots and restartable progress.
- [x] Recover/clean failed acceptance through a durable initialization intent and a grace period, protected by the execution lease.
- [x] Cover uploads arriving after lease loss/deletion and temporary/uncommitted provider data with the final lifecycle/admission policy (6.7).
- [x] Keep draft/published definitions separate from expiring execution data; bounded mutable cloud catalog with revision hashes verified on read (not separately immutable content-addressed blobs).
- [x] Verify restart, prefix isolation, missing/corrupt documents, stale writes and cleanup.

### 6.4 — Queue transport and recovery scheduling — Complete

Depends on 6.1–6.3.

- [x] Implement bounded, versioned Azure Storage Queue messages and a dispatcher that discards malformed hints without logging payloads; verify actual visibility/redelivery and stale receipt rejection.
- [x] Wire queue triggers, bounded host retries and a poison-hint consumer into the native host; generated binding metadata verified.
- [x] Verify those bindings, poison cleanup and recovery scheduling in the deployed service.
- [x] Treat queues as wake-up hints; persisted state remains authoritative, including pipeline identity used to select secret grants.
- [x] Implement bounded recovery scheduling with a one-minute Functions timer, one Table page per sweep, claim-based exclusion and authored retry timestamps; hosted restart/lost-hint/retry verification passes.
- [x] Verify lost notifications/recovery across state-store instances and Azure queue visibility redelivery; hosted duplicate acceptance and completion pass.
- [x] Exercise native worker restart, delayed retries and poison recovery end to end.
- [x] Define/verify the workplace Service Bus profile separately; preserve external RabbitMQ as third-party ingress.

### 6.5 — Shared catalog and management parity — Complete

Depends on 6.2–6.3.

- [x] Extract a deployment-store contract from the filesystem implementation and use it in the API and RabbitMQ ingress.
- [x] Implement shared drafts, concurrency tokens, immutable bundles and atomic active-route publication using Blob ETag compare-and-swap; enforce small-profile catalog limits.
- [x] Share HTTP handlers, authentication, management scopes, secret grants and history authorization between the local API and Functions. Five API contracts and nine direct Functions/configuration tests passed; hosted API and portal verification passes.
- [x] Connect the portal to the hosted management API; save/validate/test/deploy behave consistently with local use.
- [x] Verify concurrent deployment, collision handling, rollback and executions pinned to earlier versions against the live Azure catalog.

### 6.6 — Native Azure hosting

Depends on 6.1–6.5; review estimated cost against the remaining allowance before deploying compute.

- [x] Replace the legacy Functions/Redis prototype with .NET isolated native hosting and the durable adapters; build and entry-point tests pass. The final generic runtime deployment returned readiness 200 with verification-only disabled.
- [x] Compile, preview and deploy a temporary synthetic Functions Bicep profile with account-scoped managed identity and no Redis.
- [x] Finish the production Bicep profile with verified retention, health, quotas and deployment/configuration instructions.
- [x] Deploy HTTP triggers, management endpoints, queue consumers and independent recovery/retention Functions; Azure indexed all five functions and hosted execution/replay passed.
- [x] Verify the independent retention timer physically removed all preview state/documents and live versions/snapshots; data-plane inventory returned zero.
- [x] Complete retention health/drift reporting and production lifecycle enforcement.
- [x] Keep sync budgets below gateway deadlines and verify real disconnect/cancellation behavior.
- [x] Deploy Event Grid validation/delivery and verify duplicate events and authorization.
- [x] Document/provide the always-running ingress option needed for external RabbitMQ; price it separately from scale-to-zero Deldo hosting.
- [x] Verify deployment, startup, bearer management access and the explicit verification-only readiness gate against Azure.
- [x] Verify restart and hosted portal parity; production readiness requires 6.7.

### 6.7 — Mandatory internal and delivered-output retention

Depends on 6.1–6.3 and 6.6. Full contract: [storage-retention.md](storage-retention.md).

- [x] Validate positive finite configuration: 30-day defaults, pipeline policy and destination override, pinned to the effective revision/write. Connector inheritance/revision tests and live output expiry pass.
- [x] Persist output ownership and cleanup obligation **before** delivery, independent of execution history and pipeline lifetime. Independent file/Blob ledger; live lost-response and hosted history-before-output tests pass.
- [x] Capture version/ETag identity and expiry in receipts; safely reconcile uncertain/partial writes. Live Azure versions, snapshots, replacement and source-isolation test passes.
- [x] Implement independent bounded expiry sweeps with restartable deletion and per-object fault isolation.
- [x] Cover current/noncurrent versions, snapshots, soft delete, staging/orphans and auxiliary index/control records.
- [x] Install/verify native lifecycle safeguards without deleting active recovery data or unrelated/source-owned objects.
- [x] Detect permission/policy drift, immutability failures and overdue cleanup; expose failed health and actionable diagnostics.
- [x] Enforce finite active execution/retry lifetime, accepted bytes and backlog/admission limits.
- [x] Verify default/override expiry with advanced clocks and actual provider deletion; outputs outliving history, replay, replacement, source isolation and interrupted cleanup.

### 6.8 — Operations, release verification and handoff

Depends on the preceding implementation packages.

- [x] Add cloud adapter contracts and repeatable opt-in live verification to CI/test instructions; clearly separate emulator coverage from Azure evidence.
- [x] Run all relevant backend/API/portal suites and review remaining dependency advisories before external hosting.
- [x] Reproduce the three controlled integration families through the hosted installation, including restart/recovery and retention.
- [x] Measure transactions, retained bytes, compute and monitoring costs; compare the small profile with the pet-project budget.
- [x] Document installation, identity/secrets, quotas/health, backup policy, upgrades, migration, rollback, recovery and teardown. See [operations](azure-operations.md); in-flight legacy migration is explicitly unsupported.
- [x] Independently review the completed M6 changes and resolve release-blocking findings.
- [x] Record hosted behavior, security, recovery, retention, internal independent review and teardown evidence; close the bounded native Azure milestone. External review and real adoption remain below.

## M7 — Distributed work items and Redis

**Active, incomplete.** This milestone covers the Eagle concurrency/Redis requirement beyond M6's bounded Azure profile. See the [implementation plan](implementation-plan.md#milestone-7--distributed-work-items-and-redis) for scope and completion criteria, and [distributed-work-items.md](distributed-work-items.md) for detailed contracts and regression evidence. The new item runner is not enabled in the API or Functions host.

- [x] 7.1 — Implement the coordination contract and experimental Redis adapter; verify atomic item ownership, retries and bounded inspection against real Redis.
- [x] 7.2 — Implement durable manifests, checkpoints, concurrency permits and Redis-loss reconciliation; verify storage fencing and interrupted-write recovery. Load/provider-failover certification remains 7.7.
- [ ] 7.3 — Finish runtime integration. Item execution, parent coordination, bounded stage discovery, queue dispatch, whole-step result publication and synchronous real/mock parity pass CI. Host registration remains blocked on 7.5; expanded-item parity is part of 7.4.
- [x] 7.4 — Explicit YAML fan-out, bounded scheduling and ordered source/output aggregation preserve existing definitions; local real/mock tests share item semantics and accept ordinal fixtures. The sample, concurrency/retry/recovery tests and API contracts pass. See [verification evidence and limits](distributed-work-items.md#explicit-fan-out-and-local-parity-74).
- [ ] 7.5 — Implement the [item inspection/recovery design](item-inspection-recovery-design.md): paginated item inspection, operator recovery, finite physical retention, quotas and backpressure. Design and acceptance checklist drafted on 12 October; endpoints and portal controls remain to build.
- [ ] 7.6 — Add the shared immutable pipeline/route cache with deployment invalidation and correct cold-cache behavior.
- [ ] 7.7 — Verify thousands of items across independent workers, duplicate delivery, restart/failover, uncertain writes and retention; measure provider capacity and cost.
- [ ] 7.8 — Publish the distributed profile's packages, infrastructure/configuration, installation smoke tests, tutorials and versioned Eagle/Deldo handoffs.

Existing test passes establish the completed components only. Check M7 complete after the installed runtime and portal meet its end-to-end completion criteria; permanent consumer provisioning and real integration acceptance follow in their project sessions.

## Review follow-ups and adoption

### Third review — 12 October 2026

- [x] Classify Azure throttles/server errors/timeouts as retryable transport faults; fake Table 429/500/503 and recovery outage regressions pass.
- [x] Retry lease renewal until the existing local deadline; a single failed renewal leaves a 20-second connector call alive, while persistent failures still fence writes.
- [x] Separate OAuth shared fetch cancellation from waiter cancellation; caller A aborts and caller B receives the token.
- [x] Missing initialized ledgers refuse admission and report unhealthy; readiness requires an initial sweep. Add explicit fresh-install bootstrap.
- [x] Add catalog export/prune, conditional ETag route caching and batched ingress polling; exclude tombstones from live quota and test cached bundle isolation/pinning.
- [x] Reserve only after claiming; unknown recovery IDs create no blobs/reservations and contending claims cannot rotate generations. Preserve repeated event acceptance.
- [x] Remove unused Azure alias writes, shorten unconfirmed upload obligations, bound/hash streamed HTTP reads, and isolate retention error-recording failures.
- [x] Keep probes/management reads outside memory admission; busy writes return 503/Retry-After.
- [x] Prepare least-privilege Blob/SB roles, Key Vault references, monitoring/retention alert and default scale ceiling 3. Document authenticated public networking and additional monitoring cost; Bicep compiles, no deployment claimed.
- [x] Fix Redis uncertain transport outcomes and idempotent failure; keep the adapter experimental. Local Garnet, injected faults and real Redis CI pass; foundation verification was completed with the fourth-review release.
- [x] Portal preserves tokens on non-401 errors and stops polling terminal executions; regressions pass.
- [x] Correct historical evidence, capacity contingency, runtime fan-out caveats, version policy and component counts; remove the placeholder conformance CI job and stray text.
- [ ] Consumer acceptance: verify its Key Vault resolution, notification action groups and scale settings. The separate fourth-review lab now verifies fresh bootstrap, custom host role/version deletion, readiness and heartbeat.

### Fourth review — installer and release

- [x] Fix `--include dv`; run bootstrap on a fresh real account and verify a repeated invocation refuses reinitialization.
- [x] What-if, deploy and start the custom-role Functions installation; observe readiness 200 and an independent scheduled retention sweep.
- [x] Preserve health traces while sampling other telemetry; disable automatic SDK dependencies and document cap-induced false alarms.
- [x] Return 404 for unknown recovery IDs, permit catalog management despite conflicting routes, use `AzureServices` network bypass and correct HTTP limit/component-count documentation.
- [x] Push backend and portal history; repair Linux/Docker harness failures exposed by remote CI.
- [x] Verify final remote CI and publish `v0.7.26`, with Core publicly available before dependent NuGet packages. [Release](https://github.com/max-favilli/elwood/releases/tag/v0.7.26).
- [x] Repeat hosted management, queue execution, duplicate acceptance and pinned replay using the default three-instance ceiling; record reduced-ceiling failures without claiming it certified.
- [x] Record the fifth review's independent suite, release and live installer confirmation; all fourth-review fixes are closed.
- [x] Verify the local follow-up is active; schedule collection for 13 October 11:45 UTC (13:45 Rome), after the 11:15 UTC measurement-window end, and document local execution requirements/manual cleanup fallback.
- [x] Measure a complete day of maintenance `_BilledSize` by type, record heartbeat gaps/quota status, decide whether to disable performance-counter collection, and size the provisional cap with headroom and explicit workload limits. The 12–13 October UTC window recorded 30.08 MB billable telemetry; retain counters and provisional 0.1 GB/day cap (3.32 times baseline). [Full measurement and limitations](m6-release-evidence.md#full-day-telemetry-and-disposable-lab-closeout--13-october-2026).
- [x] Delete `elwood-m6-rg`, record the actual timestamped `az group exists=false`, and record the final reported Cost Management figure with billing-delay limitations; follow up delayed billing without retaining resources. Group absent at **13 October 11:59:42.8503076 UTC**; exact custom role absent too. Post-deletion reported cost **€0.10725716091221846** at **11:59:49.7272193 UTC**, not settled. Separate billing-only follow-up scheduled.
- [ ] Reconcile delayed lab billing without retaining or recreating infrastructure; preserve dated results, report a stable total after at least 48 hours and two unchanged daily observations, and pause `reconcile-elwood-lab-billing`. Do not label it a settled invoice. **14 October:** reported total increased to **€0.1348144609504452** (+€0.027557300038226734), entirely on 13 October usage rows for Azure Monitor and Storage. Only 23.78 hours had elapsed since deletion; follow-up remains active.
- [ ] Elwood observability follow-up before consumer notification acceptance: investigate the full-day sample's delayed timers and missing health traces (268 traces, maximum gap 20 minutes; host records successful runs inside that gap). Verify notification delivery against authenticated retention status; quota exhaustion was not observed. This is not an uninterrupted-heartbeat claim.

The consumer project sessions own permanent infrastructure provisioning and operating costs. This Elwood session owns reusable runtime, infrastructure, installation documentation and releases. Eagle adoption depends on M7's item-level concurrency and recovery work; the existing Blob execution lock serializes steps within an execution.

- [ ] Approve permanent hosting cost for the [verified larger-file profile](capacity-profile.md). Owner prioritises 25 MiB / 1,000 files per month over the earlier budget target; measured model projects €45–55/month excluding SQL/application hosting.
- [x] Measure the actual Deldo normalization script on 1 MiB and 25 MiB generated fixtures; establish 3 GiB managed-heap requirement for the large fixture.
- [x] Implement a 4 GiB/128 MiB document profile with per-host memory admission and bounded portal inspection.
- [x] Verify bounded accounting for 1,000 retained large-file executions and 49,000 cleanup obligations, preserving emergency capacity and generation fencing.
- [x] Complete temporary verification within the existing Azure allowance: three simultaneous large-file workers, monthly metadata maintenance, physical cleanup and measured hosting estimate. Temporary group deletion verified.
- [ ] Obtain the owner's other AI session review in addition to the completed internal independent review.

- [x] Implement [file-content duplicate suppression](content-deduplication.md): strict YAML, raw-byte SHA-256, atomic filesystem/Azure ownership, configurable 30-day history, Skipped status, explicit replay override and isolated tests. Local runtime/API/portal regressions and Azure SDK CAS simulation pass.
- [x] Verify duplicate suppression against two independently uploaded equal Blob files in the hosted capacity campaign, plus explicit forced replay.

- [ ] Prepare a versioned, tested handoff for the Eagle SAP integration session.
- [ ] Prepare a versioned, tested handoff for the Deldo integration session; resolve its hosting/event-delivery decisions using the implemented profile.
- [ ] Run both real-world acceptance exercises with their project owners and fix defects discovered there.
- [ ] Decide the next native provider and public release/support claims based on verified behavior.
## Progress record

| Date | Package | Evidence / result |
|---|---|---|
| 10 October 2026 | M7 / 7.1 Redis coordination foundation | Provider-neutral item/aggregation ownership, Lua coordination and finite expiry implemented in an experimental adapter. 23 local protocol tests pass, including 3,000 items across two clients; 227 existing pipeline tests pass. Real Redis CI added, result pending. Durable reconciliation, runtime wiring, connector fan-out and consumer release remain open in the [M7 checklist](distributed-work-items.md). No cloud resources provisioned. |
| 8 October 2026 | Baseline | M1–M5 and review follow-ups recorded in implementation-plan.md; no new rerun claimed here. |
| 8 October 2026 | 6.0 | Temporary storage lab deployed; 3 live Blob tests passed; `3b3b85b`. Actual billing query still had no rows. |
| 8 October 2026 | Checklist | Master WBS created; subsequent packages are tracked below. |
| 8 October 2026 | 6.1 | Fenced claim contract, runtime ownership-loss handling and renewable Blob claims. 202 pipeline tests passed before the final additional case; all 6 ownership cases then passed. Three simulated renewal-failure tests, 2 live Azure claim tests and 5 isolated API contracts passed. Cloud state/index/document integration remains 6.2–6.3. |
| 8 October 2026 | 6.2 / part of 6.3 | Leased Blob state, bounded Table work/expiry indexes, immutable documents, abandoned-acceptance cleanup and version-deletion journals. Six live Azure adapter tests passed; 203 pipeline tests passed. Hosted wiring, late-upload lifecycle safeguards and catalog bundles remain open. |
| 8 October 2026 | 6.4 / 6.5 | Eight queue codec/dispatch/transport tests passed, including one live redelivery test; five live catalog tests passed for concurrency, revision pinning, rollback, size limits and corrupt-control-data rejection. Pipeline suite: 203 passed. Functions bindings, shared hosted API security and Service Bus remain open. |
| 8 October 2026 | 6.5 / 6.6 | Shared HTTP/security implementation and Redis-free Functions host; five API contracts and nine direct host tests passed. Temporary managed-identity app provisioned and live test passed for draft/mock test/deploy, queue completion, inspection, duplicate acceptance and replay of the original revision. Production readiness deliberately returns 503. |
| 8 October 2026 | Recovery hardening | Abandoned synchronous tests now enter recovery indexes and settle at their deadline without repeating connector calls; v1 filesystem indexes upgrade to v2. Pipeline suite: 208 passed. One additional live Azure deadline test passed. |
| 8 October 2026 | Hosted internal cleanup | Independent five-minute retention timer removed the hosted smoke test's state and documents, including live versions/snapshots. Azure inventory: zero remaining under the preview namespace in both containers. The delayed initialization records also reached zero. Soft delete remains seven days; output retention is still open. |
| 8 October 2026 | Lab closeout for this package | Final preview package deployed and checked (200 liveness, 503 readiness, 405 wrong method); app stopped and ARM state verified. Sampled retained content ~64.3 MB, mostly packages; reported cost still EUR 0.0000201 with billing delay. Lab storage retained for remaining M6 verification. |
| 8 October 2026 | 6.7 output and artifact retention | Added independent ownership ledger, bounded admission, document/checkpoint cleanup registrations, finite execution lifetime and Azure policy/permission checks. Pipeline suite 214, connector suite 21 and hosting suite 9 passed. Live ledger/restart and output/version/snapshot tests pass. Actual Azure policy/deletion probe passes. |
| 8 October 2026 | Hosted output retention | Package `bace57e6308dacdd31f2d0bc9aa4c907b6df47873a1740eb3e138313c403456b` deployed. `m6-hosted-retention.test.mjs` passed in 472 seconds: a real test and async invocation wrote owned blobs, history expired first, then independent timer removed outputs and live versions; retention health passed. Preview remains verification-only while remaining M6 checks are completed. |

| 8 October 2026 | Final native campaign | All three hosted integration families, portal browser parity, Service Bus restart/lost-hint recovery, authored retry delay, real client abort (499/AwaitingReview), independent output retention, policy checks and quota-after-write recovery passed. Internal independent review findings resolved, including generation fencing. [Evidence](m6-release-evidence.md). |

| 8 October 2026 | M6 closeout | Final package `3805e7dca4534ee94ad6f750dcd1ddc8ff4ad693898efacba0d0984c8f514c40` deployed successfully; readiness 200, final hosted queue/replay and portal browser tests pass. Temporary resource group deletion verified (`exists=false`). Local API/portal restarted, both HTTP 200. |
| 8 October 2026 | Content deduplication / capacity | Strict optional SHA-256 suppression, 30-day history and forced replay implemented. Pipeline 227, Azure 25, Hosting 12, five API contracts, portal 120 + 10 component checks pass. Internal review findings resolved. Three final 25 MiB hosted runs completed in 49–54 seconds; duplicate skipped and replay delivered. 49,000-obligation monthly metadata model measured 14.7 seconds per scheduled sweep. Cleanup reached zero history/registrations/live output versions, leaving sources untouched. Temporary group removed (`exists=false`). [Evidence and €45–55/month projection](capacity-profile.md); permanent cost approval and actual project acceptance remain open. |
| 12 October 2026 | Third review | Five blocking regressions and the listed smaller fixes completed. Fresh Release suites/API contracts, portal tests/build and Bicep compilation pass; [detailed evidence and live consumer gates](m6-release-evidence.md#third-review-follow-up--12-october-2026). Redis foundation is included atomically with solution/CI registration; no new M7 runtime feature work and no cloud provisioning. |
| 12 October 2026 | M7 / 7.2 and initial 7.3 | Durable journal/reconciliation (`c7bf6c7`) and isolated item execution (`9058a5f`) committed and pushed. Both CI runs pass, including real Redis and Azurite; current pipeline suite 265, including 19 journal and 15 item-runner tests. Shared operations preserve pinned scripts, warnings, responses/receipts and authored retry decisions. [Detailed evidence and remaining integration tasks](distributed-work-items.md). No host activation, new cloud resources or changes to the M6 measurement lab. |
| 12 October 2026 | M7 / 7.3 scheduling and parity | Parent scheduling/recovery (`18d5546`) and whole-step binding/parity correction (`f9fbd87`) pushed; both CI runs pass. Current pipeline suite 285, including 20 scheduling regressions; five API contracts, hosting 14, Azure unit 45/integration 52 and real Redis 30 pass. Lost notifications/parent saves, concurrent sources, parent stops, corruption isolation and local real/mock equivalence verified. Host registration stays gated on item retention/recovery controls; next is 7.4. |
| 12 October 2026 | M7 / 7.4 explicit fan-out | `1fa6bf7` pushed; [all CI jobs pass](https://github.com/max-favilli/elwood/actions/runs/38266789932), including 305 pipeline tests, five API contracts, 52 Azure integration tests, 30 real Redis tests, nine connector contracts and hosted RabbitMQ recovery. Source/output expansion, bounded ordered aggregation, per-item retry receipts and real/mock fixtures verified. M7.5 API/portal design drafted; no production host activation, consumer provisioning or package publication. |
| 13 October 2026 | M6 scheduled measurement and cleanup | Exact 24-hour window preserved: 30.08 MB billable telemetry; keep counters and provisional 0.1 GB/day cap. Record 268 health traces and 20-minute maximum gap with successful host runs inside it. Final readiness/retention probes 200. Lab deletion verified 11:59:42 UTC, exact custom role absent, reported cumulative cost €0.1073 with billing delay. Measurement automation paused; separate billing-only follow-up active. M6 closeout checked; consumer observability acceptance remains explicit. No M7 work in this follow-up. |
| 14 October 2026 | Billing-only observation | Initial Cost Management 429 resolved after backoff. Exact-group query at 11:46:41 UTC reports €0.13481446, up €0.02755730 entirely on 13 October Azure Monitor/Storage rows. Dated raw query/result and daily/service comparisons preserved. Below €10 allowance; not stable or settled. Only 23.78 hours since deletion, so billing follow-up stays active. No infrastructure or M7 changes. |
