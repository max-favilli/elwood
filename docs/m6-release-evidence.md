# M6 verification and external review handoff

This document preserves the historical native Azure campaign within guarded-profile limits. Its baselines were backend `655c359`, portal `b6af335`; subsequent capacity and review evidence is explicitly separated below. The fourth-review section records the fresh installer deployment, real bootstrap and custom host-role verification. The 13 October closeout below records the complete-day telemetry measurement and its limitations. Both campaigns use temporary `elwood-m6-rg`, West Europe, within the owner's cumulative €10 allowance. No workplace production system was used.

Follow-up: [content suppression](content-deduplication.md) and the [large-file capacity profile](capacity-profile.md) are now implemented and separately verified on Azure. The evidence below remains the original guarded-profile campaign; the follow-up document records its additional tests, revised cost model and remaining permanent-hosting approval.

## Observed on 8 October 2026

- Shared HTTP/management handlers run on .NET 10 isolated Flex Functions with managed identity and Blob/Table state, without Redis/SQL runtime dependencies.
- HTTP order → OAuth2 CRM → Basic-auth Emarsys chaining passed.
- External RabbitMQ → real Azure SQL procedure → Basic-auth SAP fixture passed; repeated MessageId caused one downstream write. The shared ingress worker ran in the always-on synthetic host.
- Real BlobCreated Event Grid validation/authenticated delivery → CSV transform → SQL + owned snapshot passed: five offers, six rejected rows. Repeated event identity reused the execution; unauthenticated delivery failed.
- Hosted portal browser flow passed: sign in, create/edit, validate, mock-test, save, deploy and retire.
- Restart recovered accepted work after deliberately removing its notification. A ten-second authored retry delay was respected; dead-letter hints were cleaned without losing indexed work.
- Aborting an HTTP request after observing the external write left AwaitingReview with 499 and one remote write. Sync budget remains 60 seconds.
- Independent retention removed real-test/async outputs after their history expired. Live tests covered versions, snapshots, lost receipts, replacement/source isolation, restart, one-object permission failure and continued cleanup.
- Filling capacity immediately after a write preserved uncertainty and a terminal checkpoint, rejected resume and twelve new admissions without leaked intents, then permitted expiry/pruning. Reconciliation rotates past live entries; stale generations cannot remove renewed reservations. Incomplete ledger JSON fails closed.

Recovery evidence: `3ade4703-47fc-4974-93e4-651e815ddf53`; interrupted write: `d319eff6-55ff-4020-9bc6-2ab8c6e243ea`; hosted Deldo: `event-c4f1e7b0cffbf7342ccb24fc5ec7af94cee075a92c25af82012f0739deee9040`. These are disposable lab identities, not permanent public links.

## Reproduce checks

```powershell
dotnet test dotnet/Elwood.slnx --filter 'Category!=Integration'
dotnet build dotnet/src/Elwood.Runtime.Api -o ../.contract-runtime/review
$env:ELWOOD_TEST_RUNTIME_DLL = (Resolve-Path '../.contract-runtime/review/Elwood.Runtime.Api.dll').Path
node --test scripts/runtime-api.test.mjs scripts/durable-runtime.test.mjs scripts/connectors-api.test.mjs scripts/sync-cancellation.test.mjs
az bicep build --file infra/azure-small/main.bicep --outfile .private/main.json
```

The isolated DLL avoids stale Debug builds held by dev servers. Ordinary CI runs unit/API contracts and compiles native infrastructure. Separate Docker jobs exercise SQL/RabbitMQ/Azurite and legacy Redis; emulator coverage is not live Azure evidence.

Live tests require opt-in `ELWOOD_M6_BLOB_URL`; actual policy tests need their resource-ID opt-in too. Select `M6StorageServiceTests`, `BlobExecutionClaimTests`, `AzureExecutionStoreTests`, `BlobDeploymentStoreTests`, `StorageQueueTransportTests`, `BlobRetentionTests` and `RetentionPolicyTests`. Run Service Bus transport tests only while competing lab consumers are paused. These tests use guarded synthetic endpoints and clean their own namespaces. Missing configuration means skipped, not live-passed.

Hosted harnesses: `m6-hosted.test.mjs`, `m6-hosted-retention.test.mjs`, `m6-hosted-families.test.mjs`, `m6-hosted-recovery.test.mjs`, `m6-hosted-storage.test.mjs`; portal `scripts/m6-hosted-portal.test.mjs`. Read opt-ins/guards first. Family/recovery/transport harnesses assume ignored credential files and can restart lab apps/change synthetic pipelines. They are not production repair tools. Both native queue profiles passed; the Storage Queue test also consumed a deliberately inserted poison hint before its TTL and restored the Service Bus configuration.

## Review and remaining boundaries

Independent review here identified quota deadlock, leaked rejection intents, starved orphan cleanup and incomplete-ledger acceptance. Fixes reserve final-state capacity, preserve write uncertainty, make tombstones idempotent, reject before index creation, rotate reconciliation and validate the schema. Follow-up review found a cleanup/acceptance race, fixed by generation-conditional removal on all release paths. The final targeted review found no remaining blocker. A package-growth concern was withdrawn: inventory showed one current released ZIP with bounded version history.

Portal production dependency audit is clean after the narrow DOMPurify 3.4.16 override. Five high developer-tool findings remain in ESLint → fast-glob → micromatch → braces; latest braces 3.0.3 has no patched version. npm's suggested Next ESLint downgrade was not applied. Do not supply untrusted glob patterns to tooling; revisit upstream updates. [Advisory](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm).

The owner's other AI session is still requested to review these changes against the baselines, especially retention ownership, reserved capacity, generation fencing, policy checks, secure settings and deployment bounds. Local passes do not establish remote CI success.

Eagle/SAP and Deldo remain real-world acceptance work. Deldo needs content-hash suppression and a larger capacity profile matching its feeds: [operations](azure-operations.md), [deduplication requirement](content-deduplication.md). No all-cloud, enterprise SSO, exactly-once remote-effect or large-file performance claim is made.

## Test totals and measured usage

**Historical guarded-campaign totals:** Core 374, Pipeline 214, CLI 22, Parquet 7, Connectors 21, Azure 19 and Hosting 11 (668 total). These are not the current suite totals. The independently reviewed capacity baseline was Pipeline **227**, Azure **25**, Hosting **12**, with the other suite counts unchanged and portal 120 + 10 component tests. See the third-review entry below for the newer rerun. Five isolated API contracts passed. The historical final Azure subset passed 32 tests with one actual-policy test initially skipped for its missing resource-ID opt-in; that actual-policy test was then explicitly enabled and passed, alongside the generation regression. The subset includes unit checks: do not describe all 32 as live service tests. Separate Service Bus transport, hosted recovery, families, retention, native queue and portal browser runs supply their own live evidence.

Portal: 120 Node checks (plus the opt-in browser test skipped in the ordinary suite), **17 component tests** after the third review, production build, and lint with zero errors/three existing hook warnings. Production npm audit and the full solution's transitive NuGet vulnerability audit were clean in the recorded campaign. Developer-only npm findings are described above. Remote CI and the owner's external AI review are not claimed by that historical campaign.

An 8 October campaign snapshot listed **241,415,419 bytes (230.23 MiB)** across current/version/deleted Blob records: packages 197,387,855; catalog/control 40,901,386; state 2,931,469; documents 158,639; other containers 36,070 bytes. This inventory includes version/soft-delete records but is not a storage invoice. The rolling metric snapshot showed **31,177 storage transactions**, **491 Functions executions**, **366,899,200 MB-milliseconds = 358.3 GB-seconds**, and zero always-ready usage. At the sampled retail compute/execution rates that is about **€0.00844**, excluding all other meters and free grants. [Metric conversion](https://learn.microsoft.com/en-us/azure/azure-functions/functions-consumption-costs).

Cost Management still reported only **€0.0000265452**, plainly delayed and not the total campaign cost. The separately billed fixture B1 + SQL Basic + ACR Basic amount to about €0.67/day at sampled rates, plus builds/operations. The short campaign remains comfortably within the approved €10 based on provisioned duration and observed usage; it does not establish Deldo's monthly budget. Final test activity adds a small amount beyond this snapshot.

The generic `runtime.bicep` passed what-if and deployment against the already provisioned lab, and readiness returned 200 with verification-only disabled. The final package SHA-256 is `3805e7dca4534ee94ad6f750dcd1ddc8ff4ad693898efacba0d0984c8f514c40`. One earlier ZIP command uploaded successfully but its health check failed because the app was stopped (CLI reported an SSL EOF); starting it and checking the deployment/real readiness/hosted smoke resolved this. The final deployment command completed successfully; readiness returned 200, hosted execution/duplicate acceptance/pinned replay passed, and the final portal browser test passed in 19.5 seconds. The exact temporary group was checked by tags and inventory, deleted, and `az group exists` returned `false`. Local API and portal were restarted and both returned HTTP 200.

## Third-review follow-up — 12 October 2026

All five blockers have focused regressions: real Table SDK over a fake transport returns 429/500/503 once, recovery preserves a checkpoint during an outage, a 20-second call survives one lease-renewal failure, canceled OAuth caller A does not cancel caller B, and deleting an initialized ledger refuses admission and reports unhealthy. Catalog tests verify conditional 304 reuse/invalidation, batched active reads, independent bundle copies, export/prune with active-revision protection, and live quota after 256 tombstones.

The broader residual package covers post-lease capacity registration, unknown-ID and redelivery claims, bounded streamed HTTP sources/raw-byte hashing, Azure alias removal, unconfirmed-upload retention, per-object error isolation, memory admission/probes, experimental Redis failure semantics and portal token/polling behavior. See the checked work list in [master-checklist.md](master-checklist.md).

Verification: Core **374**, Pipeline **231**, CLI **22**, Parquet **7**, Connectors **21**, Azure **36**, Hosting **12**, and Redis injected transport **2** non-integration checks pass. The full Release run passed with Azure 35; the final additional retention-isolation regression and claim/redelivery regression passed in a targeted two-test rerun, bringing Azure to 36 distinct passing cases. Five API contracts passed using a freshly built Release DLL via `ELWOOD_TEST_RUNTIME_DLL`, avoiding the stale Debug server. A separate loopback Garnet run passed all **27** Redis tests (25 protocol cases plus those same two injected cases); this does not substitute for real Redis 7.4 CI. Portal **120** node tests and **17** component tests pass; TypeScript and production build pass; lint has zero errors and three pre-existing hook warnings. The opt-in live portal test was skipped.

`az bicep build` compiles the updated installer. The explicit retention bootstrap script passes PowerShell parsing. No Azure resources were created and none of the previous live service results were rerun: the lab is gone. Consumer installation must verify the new custom Blob role and Sender/Receiver grants, Key Vault references, first-sweep readiness, monitoring/alert delivery and scale ceiling. Removing old Owner grants is an explicit upgrade step; incremental ARM deployment does not remove them. The storage/SB public endpoints are documented as authenticated public networking, not private connectivity. Monitoring charges are additional to the historical capacity estimate.

The experimental Redis source/test directories are included with the solution and CI registration; runtime fan-out and durable reconciliation remain unimplemented. The echo-only conformance job was removed: ordinary Core/TypeScript suites remain the executable language checks. No DSL behavior changed. This review package is ready for another code review; it is not a claim of a newly verified Azure deployment.

## Fourth-review installer verification — 12 October 2026

The third-review paragraph above describes its historical state. For this follow-up a fresh `elwood-m6-rg` was created in West Europe. Azure Cost Management reported **€0.05287948** for the previous campaign when checked before creating it (billing is delayed). The new lab contains only the native Functions/storage/monitoring installation; no SQL, RabbitMQ, Redis or always-ready instances. It uses `files-25m`, one maximum instance per scaling group, and the same account-scoped runtime/host identity. The approved campaign allowance remains €10.

Backend application source: `881a32c`; package SHA-256: `12c6f12c3ed0f767504799473394b83f1e27f20a3168f908a06a40e711a42860`. Deployment `fourth-review` returned **Succeeded** after a real what-if reported **26 Create** changes. App: `elwood-review-api-r3m5auolhstum`; account: `elwr3m5auolhstum`. Its identity has the custom Blob contributor/version-cleanup role, Queue Data Contributor, Table Data Contributor and Reader. It has **no Blob Data Owner** grant. The operator separately received Blob Data Contributor on this disposable account to run bootstrap. Deployers need `Microsoft.Authorization/roleDefinitions/write` at the resource group and role assignment permission; Contributor alone is insufficient.

The corrected script was run with real Azure CLI **2.86.0**:

```powershell
./scripts/initialize-azure-retention.ps1 -SubscriptionId <approved-subscription> -StorageAccount elwr3m5auolhstum -CapacityProfile files-25m
```

Actual output (middle partition lines omitted):

```text
Initialized elwood/retention/00.json
...
Initialized elwood/retention/63.json
Initialized elwood/retention/health.json
Initialization complete. Start workers and require a successful retention sweep before readiness.
```

Listing with `--include dv` found **66 objects**: 64 partitions, health and the final layout marker. A second script invocation stopped with `Namespace already has data/versions in control. Restore existing control data or choose a fresh namespace; do not reinitialize.` No ledger was overwritten.

The published ZIP was deployed through `az functionapp deployment source config-zip` (exit 0; deployment ID `afa3ec34-6746-4835-8811-5137f753500a`), followed by `az functionapp start`. ARM reported Running and all seven functions registered. Initial probes timed out while the HTTP group started; liveness then returned 200, and readiness initially returned 503 before the first sweep. At **10:48:21 UTC**, a Node HTTP probe recorded:

```text
GET /api/health     200  {"status":"healthy",...}
GET /api/readiness  200  {"status":"ready"}
GET /api/retention  200  storage.healthy=true, overdueObjects=0, failures=[]
```

Retention's ledger recorded `lastSweepAt=2026-10-12T10:45:00.6458695+00:00`; Application Insights recorded `ELWOOD_RETENTION_HEALTHY` at **10:45:00.729 UTC**. Host storage contains timer monitor/lease records for ExecutionRecovery and ExecutionRetention. Readiness also confirmed the live lifecycle policy and managed-identity deletion of probe versions/snapshots. These establish the custom role for this deployment's host requirements; they are not a claim that it covers every Functions binding.

Fourth-review regressions cover unknown recovery IDs, duplicate-route management repair, and the telemetry sampling/dependency configuration. The complete local non-integration run passed Core **374**, Pipeline **231**, CLI **22**, Parquet **7**, Connectors **21**, Hosting **13**, Azure **37**, Redis injected transport **2**. All **five API contracts** passed against the explicit Release DLL. Backend and portal history were pushed. Portal [CI](https://github.com/max-favilli/elwood-portal/actions/runs/38237759336) passed its tests, lint, type check and build. The first backend CI run exposed two previously unexercised harness defects: SIGABRT cleanup awaiting a second exit event on Linux, and duplicated Azurite listener arguments. Commit `2f77e4b` fixes them; its complete [backend CI](https://github.com/max-favilli/elwood/actions/runs/38238628431) is green. Docker adapter tests passed **26** cases, with **25 live-Azure opt-ins skipped**; a separate real Redis run passed **25** coordination cases. SQL/Blob/RabbitMQ contracts, hosted RabbitMQ recovery, .NET/API/Bicep and TypeScript jobs also passed.

Monitoring is configured with adaptive sampling, unsampled traces/exceptions and automatic SDK dependency tracking disabled. A capped workspace can still cause a false retention alert; consult live retention status and ingestion health. A short initial observation is **not a measured day**. The complete 24-hour maintenance measurement, cap sizing and disposable lab teardown remain open and must be recorded before claiming this follow-up fully closed. Consumer traffic and notification action groups still need consumer-specific validation.

Additional live smoke: reducing the lab ceiling to one instance produced intermittent readiness timeouts (including a 60-second 503); isolated probes still passed. Restoring the template's **default ceiling of three** allowed the full `m6-hosted.test.mjs` flow to pass in **190.4 seconds**: management denial/authentication, synchronous draft test, deployment, durable Storage Queue execution, inspection, duplicate acceptance, pinned replay and retirement. Pipeline `host-6703e8d5-0bd7-4545-a3fa-e972c1dd821e` transformed 41 to 42; the repeated event reused execution `event-6b5035c27460cd0be93eb713beb95624d87973383a4f3e7ade6fae81e681e667`, and replay `88af9002-b10a-44fb-8bee-18ae2a41783f` retained its original revision. This does not establish a root cause for the one-instance timeouts or certify that reduced configuration. The lab remains at three, with no always-ready instances, for the scheduled maintenance measurement.

Release [v0.7.26](https://github.com/max-favilli/elwood/releases/tag/v0.7.26) was published at **11:09:49 UTC**, from `979b91b`, after all release jobs passed. Core was accepted by NuGet at **11:04:08**, became publicly downloadable at **11:09:25**, and only then were Pipeline, Pipeline.Azure and CLI 0.7.26 pushed at **11:09:27–29**. The release includes Linux, macOS and Windows native CLI assets; the API container workflow also passed. Json/TypeScript retain their separately documented versions. The first release run hit the existing 50 ms wall-clock benchmark assertion (69 ms on the shared runner); retry passed. Follow-up `979a0f1` replaces that timing threshold with an exact count of evaluated items, without changing DSL behavior, and drains hosted probe response bodies.

A thread follow-up is scheduled to measure **12 October 11:15 UTC through 13 October 11:15 UTC**, allow ingestion delay, record maintenance-only volume and heartbeat gaps, check costs, size the cap with headroom, and delete only `elwood-m6-rg` after checking its tags/inventory. It must verify the group is gone and pause itself. The last reported campaign cost is still **€0.05287948**, excluding charges not yet reported. The lab remains allocated specifically for that measurement; no permanent consumer infrastructure was created.

Post-publication verification: all four 0.7.26 packages returned HTTP 200 from NuGet's public flat-container feed. A fresh consumer with an isolated package cache and only `PackageReference Include="Elwood.Pipeline.Azure" Version="0.7.26"` restored and built for **net8.0 and net10.0**, with zero warnings/errors. Running it on net10.0 exercised `StepEvaluator.IsScript`, including the string-literal case, through the published Core `Lexer.FirstToken` API. Pipeline's published nuspec requires Core 0.7.26 for both target frameworks. The test-only follow-up `979a0f1` also passed [full CI](https://github.com/max-favilli/elwood/actions/runs/38240812736).

## Fifth-review confirmation and remaining closeout — 12 October 2026

The owner's independent reviewer reported matching suite totals (**374/231/22/7/21/13/37/2**), green CI and all four public 0.7.26 NuGet packages with the correct Core dependency. Direct lab inspection confirmed the custom Blob role plus Queue/Table/Reader grants only, a three-instance ceiling, the 0.1 GB/day ingestion cap and readiness 200. The reviewer observed **13 consecutive `ELWOOD_RETENTION_HEALTHY` traces from 10:45 through 11:45 UTC**, without gaps; all 131 dependency records were InProc. These are attributed independent observations, not an additional rerun by this session. All fourth-review fixes are closed.

The reviewer also reported this preliminary three-hour idle ingestion sample, using `_BilledSize` (rounded MB as supplied):

| Telemetry type | MB |
| --- | ---: |
| AppPerformanceCounters | 0.83 |
| AppTraces | 0.65 |
| AppMetrics | 0.46 |
| AppDependencies | 0.12 |
| Total | 2.06 |

This is not the requested full-day measurement or a Cost Management charge. Performance counters remain enabled during the baseline. The closeout must report the complete day's bytes by type and explicitly decide whether to disable `EnablePerformanceCounterCollectionModule`; the retention alert does not use those counters. Any change must preserve the health traces and have focused verification. Cap sizing must retain headroom and distinguish idle maintenance from consumer workloads.

The existing local follow-up `complete-elwood-telemetry-measurement` was verified active and changed from a 25-hour interval to **13:45 Europe/Rome daily**, making its next intended run **13 October 2026 at 11:45 UTC**. **11:15 UTC is the measurement-window end, not the cleanup start**; the extra 30 minutes allow ingestion delay. The follow-up will collect the fixed 24-hour window, preserve evidence locally, delete only the approved disposable group, record `az group exists=false` with timestamp, and query Cost Management for the final reported campaign figure. Delayed billing must be labelled and followed up without retaining or recreating resources. It pauses after closeout is complete.

This is a local scheduled task, not an Azure-side deletion guarantee: the PC and app must remain running, Azure authentication must remain valid, and unattended permissions must permit the operations. See [scheduled-task requirements](https://learn.chatgpt.com/docs/automations?surface=app). The owner was informed of this dependency. If the local follow-up cannot run, the owner must arrange manual deletion of `elwood-m6-rg` after the measurement; cleanup remains unverified until an actual `az group exists` result is recorded. M6 stays unchecked until full-day telemetry, the performance-counter decision, deletion confirmation and cost evidence are recorded. The optional redundant release-step condition is left unchanged.

## Full-day telemetry and disposable-lab closeout — 13 October 2026

The scheduled follow-up ran after the ingestion allowance. Queries used the exact half-open UTC window **`2026-10-12T11:15:00Z <= TimeGenerated < 2026-10-13T11:15:00Z`**. Collection began at **11:52:10 UTC on 13 October**, more than 37 minutes after the window ended. The last recorded ingestion timestamp among the four populated application telemetry tables was **11:15:39.2177093 UTC**. No telemetry collection configuration was changed during the baseline. ARM still reported `maximumInstanceCount: 3`, 4,096 MiB workers and no always-ready allocation.

These are actual sums of `_BilledSize`, not portal charts or an extrapolation of the reviewer's preliminary three-hour sample. An all-table `search *` cross-check found the two additional nonbillable tables below; a second query checked `_IsBillable` explicitly. AppRequests and AppExceptions were absent from the window, rather than assigned an estimated volume.

| Type | Records observed | `_BilledSize` bytes | Billable bytes |
| --- | ---: | ---: | ---: |
| AppPerformanceCounters | 15,693 | 11,990,272 | 11,990,272 |
| AppTraces | 7,589 | 10,004,548 | 10,004,548 |
| AppMetrics | 8,384 | 6,470,492 | 6,470,492 |
| AppDependencies | 1,711 | 1,618,606 | 1,618,606 |
| AppRequests | Absent | — | — |
| AppExceptions | Absent | — | — |
| Usage | 101 | 42,212 | 0 |
| AppSystemEvents | 1 | 4,419 | 0 |
| **Total observed** | **33,479** | **30,130,549** | **30,083,918** |

Total billable telemetry was **30.083918 MB = 28.690260 MiB = 0.030083918 decimal GB**. Total `_BilledSize`, including the nonbillable records, was **30.130549 MB**. These bytes are not a Cost Management charge. All 1,711 dependencies were **InProc / Invoke**, SDK `azurefunctions-netiso: 2.51.0+7933fccdc110be8c4808cc8d3e8ae22142`; none were SDK HTTP/storage dependency records. No failed dependency record was observed. Automatic SDK dependency tracking remains disabled, although SDK error messages can still appear in AppTraces.

**Heartbeat and quota limitations.** There were **268** `ELWOOD_RETENTION_HEALTHY` traces, against 288 nominal five-minute slots. **17 gaps between consecutive heartbeats exceeded 330 seconds**; the maximum was **1,200.3991091 seconds (20 minutes)**, from **13 October 05:40:00.4192278 to 06:00:00.8183369 UTC**. There were no `ELWOOD_RETENTION_UNHEALTHY` traces in the measured window. Host logs contained **276 successful ExecutionRetention completions**, eight without a corresponding worker health trace, and 13 `IsPastDue` trigger-detail records. In the longest gap, the host recorded successful retention at 05:45:00.4157264 and 05:56:17.7883012; the latter was a past-due invocation for 05:50. Thus the heartbeat gap does not establish that cleanup stopped for 20 minutes. It does establish that this installation did **not** demonstrate uninterrupted five-minute health telemetry. Delayed scheduling and missing worker traces are observed; their underlying cause is not established.

The workspace reported `dailyQuotaGb: 0.1` and `dataIngestionStatus: RespectQuota`; its returned `quotaNextResetTime` was a stale **12 October 20:00 UTC** and is not treated as proof of the next reset. The window's quota/data-collection Operation query returned no records. There is no evidence here of quota exhaustion causing the gaps. A final live probe at **11:54:44 UTC** returned readiness **200**; retention returned **200**, `healthy=true`, `overdueObjects=0`, no failures, and `lastSweepAt=11:50:00.4518314+00:00`. The heartbeat-based alert can therefore also have false positives from delayed/missing telemetry, independently of workspace capping. Consumer acceptance must investigate that delivery gap and compare alerts with authenticated retention status before relying on unattended notifications.

**Performance-counter and cap decision:** keep `EnablePerformanceCounterCollectionModule` at its existing enabled default. Counters account for **39.86%** of this day's billable bytes, but retaining resource diagnostics is useful while investigating host/timer behavior. Removing every observed counter would save at most **11.99 MB/day (0.360 decimal GB per 30 days at this rate)**; that is an upper bound, not a measured result of disabling the worker module, which need not control every host counter. For this baseline, removing those diagnostics solely for ingestion savings is not worthwhile. No collection setting was changed, so no changed-setting/live redeployment verification is claimed.

Retain the provisional **0.1 GB/day** cap. Using a conservative decimal 100 MB comparison, it is **3.32 times** the measured daily billable volume, leaving **69.92 MB/day** above this baseline. This is a maintenance-only sizing observation on the disposable installer, with no representative supplier traffic. It is not a validated cap for Eagle, Deldo, concurrent workloads or failure bursts. Consumers must repeat the measurement with their workload and approve monitoring costs; a cap is neither a spending guarantee nor reliable protection for health-trace delivery.

Raw queries/results, the full AppTraces window, hourly volumes, system-event details, workspace/quota state, group tags/inventory, scale configuration, live probes and costs were preserved before deletion under ignored **`.private/fourth-review/closeout-*`** files. `closeout-manifest.json` records SHA-256 hashes of the then-current evidence files. No credentials or raw private telemetry are committed. One initial wildcard `union *` query failed with `SemanticError: Failed to resolve ... TimeGenerated`; explicit tables and the independent `search *` query succeeded.

**Cleanup completed.** Immediately before deletion, the exact group in subscription `79522cd2-4487-4289-b0b8-42ee8aa5d649` had tags `project=elwood`, `purpose=fourth-review`, `temporary=true`. Its eight resources were the review identity, storage account, Flex plan, Functions app, Log Analytics workspace, Application Insights component, retention alert and Application Insights Smart Detection action group. Every inventory ID was inside that group; none was a protected resource. The approved `az group delete --name elwood-m6-rg --subscription 79522cd2-4487-4289-b0b8-42ee8aa5d649 --yes --no-wait` request was accepted at **11:56:15.7928677 UTC**. The first existence check at 11:57:19 still returned true. At **11:59:42.8503076 UTC**, the actual command returned:

```text
az group exists --name elwood-m6-rg --subscription 79522cd2-4487-4289-b0b8-42ee8aa5d649 -o json
false
```

Lookup of the exact custom role **`7c0fbb87-0bb2-5b7c-a7db-2818a22d3848`** returned `[]`, confirmed by a second lookup. It did not remain behind, so no separate role deletion was needed. No resources were retained for billing and no consumer infrastructure was created.

**Final reported campaign cost at cleanup:** **€0.10725716091221846** (approximately **€0.1073**), as of **13 October 2026 11:59:49.7272193 UTC**, from an unpaginated Cost Management `ActualCost`/`PreTaxCost`, month-to-date query with the exact `ResourceGroupName=elwood-m6-rg` filter. This includes earlier campaigns under the same group name and returned usage dates through 13 October. The query immediately before deletion returned the same total. It remains well below the cumulative €10 allowance.

| Service | Reported EUR, rounded |
| --- | ---: |
| Azure Monitor | 0.02989420 |
| Storage | 0.02809272 |
| Azure App Service | 0.02694135 |
| SQL Database | 0.01180629 |
| Container Registry | 0.01050677 |
| Service Bus | 0.00001584 |
| Bandwidth, Event Grid, Functions, Log Analytics | 0.00000000 each |

These are **reported charges, not a settled final invoice**. Same-day records can be incomplete or revised; reported zeros do not establish free ongoing hosting. A separate billing-only follow-up, **`reconcile-elwood-lab-billing`**, is active daily at **13:45 Europe/Rome**, beginning 14 October. It will query only the deleted group's costs, preserve dated results, update this evidence, and pause after at least 48 hours since deletion and two unchanged consecutive daily observations including 13 October usage. Even then the figure will be labelled stable reported cost, not settled billing. It must not recreate any infrastructure.

The measurement, explicit counter/cap decision, verified deletion and final reported cleanup cost complete the M6 installer/release closeout. The original **`complete-elwood-telemetry-measurement`** automation is now **PAUSED**; only the separate billing follow-up remains active. Billing reconciliation and the explicitly recorded consumer observability/notification acceptance remain separate follow-ups. No runtime configuration or M7 feature work was changed during this closeout.
