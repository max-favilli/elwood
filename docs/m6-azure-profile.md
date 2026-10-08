# M6: small Azure profile and verification estimate

Current implementation and operations: [native Azure installation](../infra/azure-small/README.md), [limits and runbook](azure-operations.md), [M6 evidence](m6-release-evidence.md). The chronological design/lab notes below describe earlier stages and are superseded by those documents where status differs.

Status: implementation/design work has started after the second-review package. The temporary storage-only lab is deployed and three live Blob service probes pass (8 October 2026). No Elwood cloud runtime is deployed yet. The existing Functions/Redis prototype does not satisfy M6's durable ownership, catalog or retention contract and is not a release-ready deployment target.

## Cost proposal

The maintainer approved the temporary `elwood-m6-rg` and **€10 total** on 8 October 2026; ask before exceeding that allowance. The estimate for a short campaign with synthetic payloads, limited retries and cleanup afterwards remains **€2–5 expected**. The initial storage-only template should be well below €1/month at <=1 GiB and <=100,000 requests. These are engineering planning allowances, not Azure quotations or enforced spending caps; exclude tax and currency/offer differences.

A provisional light-use Elwood footprint is **€3–8/month**, excluding the business application's SQL database/application and a separately hosted portal. The pet project's €3–5 Elwood target remains something to measure, not a guarantee. For verification the portal stays local. The proposed production baseline is native .NET 10 isolated Functions on Flex Consumption, Blob documents/bundles, Table metadata/indexes, Storage Queue notifications, Event Grid and managed identity. No Redis, always-ready instance, dedicated App Service plan, AI resource or private endpoint is assumed.

| Item | Estimate assumption / limit |
|---|---|
| Compute | 1,000 executions/month, roughly 30–120 seconds of cumulative 2 GiB worker time per execution: 60,000–240,000 GiB-seconds before retries and maintenance. This duration is an assumption to benchmark. Include cold starts, retries, recovery and cleanup. |
| Retained bytes | 1–10 MiB typical CSV; normalization can expand 2–3x; each input/intermediate/output copy and older Blob version counts. At the upper end this can mean tens of GiB retained for 30 days, plus soft-delete overhead. |
| Queue/Table/Blob operations | Use bounded due/expiry queries; no one-second full-store scan. Instrument transactions per execution and per sweep. |
| Monitoring | Sample and bound ingestion, finite retention, no payload logging. A runaway log stream must not be allowed to dominate the budget. |
| Event Grid / egress | Low event volume; include retries, operation billing and returned/downloaded data. |
| Portal | Local during verification. Price an actual cloud portal separately before including it in the small-profile budget. |

Published grants are subscription-wide. Other projects may consume them, so record costs both with and without an available free grant. Microsoft documents Flex's on-demand charging/grants in [Functions pricing](https://azure.microsoft.com/en-us/pricing/details/functions/); [Blob](https://azure.microsoft.com/en-us/pricing/details/storage/blobs/), [Table](https://azure.microsoft.com/en-us/pricing/details/storage/tables/), [Queue](https://azure.microsoft.com/en-us/pricing/details/storage/queues/) and [Event Grid](https://azure.microsoft.com/en-us/pricing/details/event-grid/) are separate meters. Obtain a region/currency-specific calculator quote before deploying compute. Budget alerts do not stop consumption; [Azure budget behavior](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/tutorial-acm-create-budgets).

## Read-only preflight — 7 October 2026

The account returned the maintainer's expected personal subscription and tenant, state Enabled. The West Europe Flex runtime list includes dotnet-isolated 10.0 with 512/2048/4096 MiB instance options. Microsoft.Storage and Microsoft.EventGrid returned NotRegistered. Bicep 0.48.1 is already installed. No provider registration, group creation, role assignment, resource creation, login or production-resource inspection was performed.

The first provisioning unit is [isolated contract-test storage](../infra/azure-small/README.md), group `elwood-m6-rg`. The approval described above covers this temporary lab; unrelated production groups, plans, databases, identities and services remain out of scope.

## Live lab verification — 8 October 2026

Microsoft.Storage registration, group creation in West Europe, the scoped what-if preview and deployment all succeeded. Readback confirms Standard LRS StorageV2, HTTPS/TLS 1.2, disabled anonymous Blob/shared-key access, Blob versioning, seven-day Blob/container soft delete and the lab-only lifecycle rule. Queue and Table authenticated reads succeeded with empty results. No compute, Event Grid, SQL, Redis or hosted portal was provisioned.

The first two live probes failed with `OperationNotAllowedOnAutomaticSnapshot`: Blob Data Contributor lacks `deleteBlobVersion/action`. The corrected template grants Blob Data Owner on the new test account only. The redundant contributor grant from the first deployment was removed and the three intended data roles were verified. Production cleanup must explicitly include version-delete rights; a base-blob delete is insufficient evidence of retention.

The corrected live run passed **3 tests, 0 failures, 0 skips** in 23 seconds:

- Concurrent lease acquisition is rejected, renewal succeeds, and an owner whose lease was forcibly broken cannot overwrite the new owner's data.
- The same stale-write protection holds after natural expiry of the renewed 15-second lease.
- An old ETag cannot delete a replacement; an older version can be deleted independently without changing the replacement's content or ETag.

Every test verifies cleanup of its current blob and live versions. The first run's four leftover versions were also deleted explicitly. Final inventory across all three containers found **0 live objects**, **10 soft-deleted versions totaling 100 content bytes**, and no objects in the documents/output containers. Soft-deleted content follows the seven-day configured window; physical reclamation was not observed. The resource-group-scoped month-to-date cost query succeeded with no rows, so an actual charge is not yet available. This does not establish zero cost. The lab remains allocated for subsequent M6 adapter verification and must be removed when that verification is finished.

These results verify service behavior and provisioning permissions. They do not certify the still-pending runtime adapters, 30-day production cleanup, queue redelivery, Table concurrency, hosted endpoint delivery or managed-identity hosting. Testcontainers' pre-existing SSH.NET advisories remain; no Docker, RabbitMQ or Erlang software was installed for these probes.

## Runtime design inputs

1. Persist acceptance intent before acknowledging, and recover the gap between payload, state/index and queue writes. Queue messages are wake-up hints, not the sole recovery record.
2. Use storage-enforced conditional writes and lease identity. WBS 6.1 now provides `IExecutionClaim`, explicit claim-bound state writes/deletes, ownership-loss cancellation and `BlobExecutionClaim`. The engine validates ownership before/after external calls and leaves the last durable checkpoint for the new owner on loss. Live tests verify renewal and stale-writer rejection. Cloud state/index and immutable document adapters are verified below; final lifecycle and hosted crash tests remain open. External exactly-once delivery remains destination-dependent.
3. Keep large execution/attempt documents in Blob storage. Table entity limits make a single unbounded serialized ExecutionState unsuitable. Bound metadata/attempt history and index queries. Use due/expiry partitions and paging; skip/log individual corrupt records while reporting adapter health.
4. Shared immutable bundles, draft concurrency tokens and atomic active-route publication now use the Blob catalog described below. Management permissions and scoped secrets must apply equally to Functions and local API hosts.
5. Keep the 60-second sync budget below the gateway deadline; persist it and link request/host cancellation. Azure HTTP-triggered Functions have a [230-second response ceiling](https://learn.microsoft.com/en-us/azure/azure-functions/functions-scale). Test actual disconnect behavior under the selected Functions integration; framework cancellation propagation must not be assumed.
6. Output cleanup obligations must be independent of execution history. Register ownership/expiry before writes, retain version/ETag identity, and never delete a replacement or source-owned file. Apply [the retention contract](storage-retention.md) to versions, snapshots, orphan acceptance, soft delete and failed/partial uploads. Native lifecycle safeguards must not delete active recovery data.
7. Bound active execution lifetime, accepted bytes, retry/attempt counts and cleanup backlog. Refuse new work visibly if cleanup/ownership guarantees cannot be met; do not silently grow forever or delete active inputs to make space.
8. Verify Storage Queue and Service Bus profiles separately. External RabbitMQ subscriptions may need an always-running ingress worker; that is a workplace-oriented hosting cost, not part of the scale-to-zero Deldo estimate.

## M6 completion evidence still required

Provider contract tests; native Functions/API/portal parity; all three real integration families; live Event Grid subscription validation and duplicate delivery; managed identity and deployment authorization; crash/lease-loss/replay tests; actual default and overridden retention cleanup with drift detection; bounded growth/health; deployment, upgrade/rollback and recovery instructions; and measured service costs. The storage-only lab and passing M5 tests do not close M6.

Current task status is maintained in the [master checklist](master-checklist.md). WBS 6.1 verification added six runtime ownership cases, three simulated renewal-failure/cancellation cases, two live Azure claim tests, and five passing API contracts against an isolated runtime build. Azure reported EUR 0.0000201 in the resource-group cost query on 8 October 2026; billing delay means this is not the final campaign cost.

WBS 6.2 and the document portion of 6.3 now add `AzureExecutionStateStore` and `AzureExecutionDocumentStore`. State is committed to a leased Blob; Table work/expiry records precede the checkpoint and are verified against its atomic metadata. Each recovery/retention call reads one bounded index page, rotating its continuation rather than repeatedly loading the oldest corrupt record. History has a separate index. Large state/document reads have byte limits. Unfenced state writes are rejected.

Execution documents use unique physical references even when the logical key is reused. Failed acceptance has an initialization record written before Blob creation; a ten-minute grace period and an execution claim protect cleanup. State-version deletion has independent Table records so interruption after deleting the base Blob does not lose cleanup obligations. Six live tests passed for cross-instance recovery, duplicate acceptance, replay/expiry, immutable references, corrupt-record isolation, abandoned acceptance and reconciliation while a writer holds its lease. The 203-test pipeline suite also passed. These adapters are not wired into a released cloud host yet; final late-upload/native lifecycle safeguards, operational quotas, catalog bundles and delivered-output retention remain open.


## Queue and catalog adapters — 8 October 2026

`StorageQueueStepQueue` carries a versioned hint limited to 4 KiB, with Base64 transport and a finite seven-day TTL. `QueuedExecutionDispatcher` takes the pipeline identity from accepted execution state before selecting secret grants. Duplicate/completed messages cannot repeat completed steps; invalid messages are discarded without logging their contents. Transient storage/ownership failures propagate for host retry. Eight tests passed, including a live receive → visibility expiry → redelivery → acknowledgement test. Azure rejected the superseded pop receipt with `MessageNotFound`; acknowledgement with the current receipt succeeded. Its temporary queue was removed. Native Functions bindings, poison cleanup and the Service Bus profile are still pending.

`IDeploymentStore` now abstracts the API and RabbitMQ catalog. `BlobDeploymentStore` implements the small Azure profile with one control Blob whose ETag atomically covers the draft, immutable revisions and active routes. Concurrent writers re-read and revalidate on conflicts; route matching captures the same committed revision and authentication policy. Corrupt control data fails closed. Definitions are separate from expiring execution payloads. Bounds are 4 MiB serialized catalog, 256 pipeline IDs including retired IDs, and 128 retained revisions per pipeline. Reaching a bound rejects the mutation without changing the published catalog; a larger installation needs an explicit catalog upgrade, not silently unbounded history. Native version retention for the control container remains a hosting requirement.

Five live catalog tests passed: concurrent draft writers, concurrent route collision, captured old execution/rollback/deletion, oversized/unsafe/unsupported changes, and corrupt data. Tests removed their catalog Blobs and live versions. The API was built into an isolated folder to avoid stale DLLs held by development servers. The complete pipeline suite passed 203 tests. Cloud management authorization/portal and native host wiring remain open in the master checklist.

## Native preview verification — 8 October 2026

The local API and Functions now share `Elwood.Runtime.Hosting`: handlers, request binding, management authorization, secret/connector grants and inspection. Functions uses an explicit HTTP dispatcher because [isolated ASP.NET integration does not expose the normal middleware/routing pipeline](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide#aspnet-core-integration). Nine direct Functions/configuration tests and five API contract tests passed. The Azure profile rejects local development credentials and missing management identities, limits sync budgets below the gateway deadline, and currently refuses startup without an explicit synthetic-verification setting. `/api/health` is liveness; `/api/readiness` returns 503 for this preview.

The temporary `test-runtime.bicep` deployment succeeded in `elwood-m6-rg`: Flex Consumption, 512 MiB, no always-ready instances or Redis, user-assigned managed identity with data roles on the lab account only, separate catalog/package containers, HTTPS and disabled basic publishing. All five functions were indexed: HTTP, execution step, recovery, retention and poison hints. HTTP liveness returned 200. No SQL, broker, Event Grid subscription or hosted portal was created by this package.

The hosted smoke test passed in 21 seconds. It verified unauthenticated management denial, authenticated authoring, a draft invisible to triggers, an explicit mock test of an async definition, explicit deployment, queue-driven completion, inspection returning 42 from input 41, duplicate acceptance using the same execution ID, and replay retaining the original revision after a new deployment. The test retired its pipeline. Its three executions had five-minute retention. The independent retention function subsequently removed **all live state/document blobs and their versions/snapshots** under the preview namespace; Azure data-plane inventory returned zero in both containers. Soft-deleted bytes still follow the seven-day lab window. Auxiliary initialization records have a separate ten-minute grace period; a later Table inventory also confirmed zero preview index rows.

During inspection, an additional recovery gap was fixed: a crashed synchronous draft test (`durable=false`) could be absent from recovery. Such deadlines are now indexed and settled without further connector calls, including uncertain-write classification. Filesystem indexes upgrade from v1 to v2. The full pipeline suite passed 208 tests; an additional live Azure deadline test passed. These results do not claim gateway-disconnect behavior or native process-crash recovery has been exercised yet.

The production installation, delivered-output retention, lifecycle/admission/health enforcement, Service Bus/always-running ingress profile, live Event Grid subscription, portal parity and full controlled integration campaign remain open. The preview cannot be used as a first-release installation; see the master checklist for exact release gates.

## Historical preparation verification

Before deployment, the storage Bicep compiled locally without warnings/errors. The original two opt-in service probes compiled and were reported as **skipped** because no approved live endpoint was configured. The live results above supersede that preparation status. The Azure test project's existing Testcontainers dependency still brings SSH.NET security advisories; no Docker or broker software was installed for this preparation.

Backend review follow-up: `be7b6c2`. Portal review follow-up: `b6af335`. Both are local commits; neither was pushed during this work.

The final preview package was deployed after the additional recovery fix. Azure returned liveness 200, verification-only readiness 503 and the expected 405 for an unsupported method. The app was then stopped; ARM readback confirmed `Stopped`. No further compute work is scheduled between sessions. Lab storage remains for M6 verification: the sampled containers listed about 64.3 MB of content including versions/soft-deleted data, mostly deployment packages. This is an object inventory, not a complete billed-byte measurement. The last group cost query still reported EUR 0.0000201; compute billing has not necessarily arrived.
