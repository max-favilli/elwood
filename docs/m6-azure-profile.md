# M6: small Azure profile and verification estimate

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
2. Use storage-enforced conditional writes and lease identity. WBS 6.1 now provides `IExecutionClaim`, explicit claim-bound state writes/deletes, ownership-loss cancellation and `BlobExecutionClaim`. The engine validates ownership before/after external calls and leaves the last durable checkpoint for the new owner on loss. Live tests verify renewal and stale-writer rejection. Cloud state/index and immutable document integration remain necessary before advertising end-to-end cross-worker safety. External exactly-once delivery remains destination-dependent.
3. Keep large execution/attempt documents in Blob storage. Table entity limits make a single unbounded serialized ExecutionState unsuitable. Bound metadata/attempt history and index queries. Use due/expiry partitions and paging; skip/log individual corrupt records while reporting adapter health.
4. Shared immutable bundles, draft concurrency tokens and atomic active-route publication must replace the filesystem catalog. Management permissions and scoped secrets must apply equally to Functions and local API hosts.
5. Keep the 60-second sync budget below the gateway deadline; persist it and link request/host cancellation. Azure HTTP-triggered Functions have a [230-second response ceiling](https://learn.microsoft.com/en-us/azure/azure-functions/functions-scale). Test actual disconnect behavior under the selected Functions integration; framework cancellation propagation must not be assumed.
6. Output cleanup obligations must be independent of execution history. Register ownership/expiry before writes, retain version/ETag identity, and never delete a replacement or source-owned file. Apply [the retention contract](storage-retention.md) to versions, snapshots, orphan acceptance, soft delete and failed/partial uploads. Native lifecycle safeguards must not delete active recovery data.
7. Bound active execution lifetime, accepted bytes, retry/attempt counts and cleanup backlog. Refuse new work visibly if cleanup/ownership guarantees cannot be met; do not silently grow forever or delete active inputs to make space.
8. Verify Storage Queue and Service Bus profiles separately. External RabbitMQ subscriptions may need an always-running ingress worker; that is a workplace-oriented hosting cost, not part of the scale-to-zero Deldo estimate.

## M6 completion evidence still required

Provider contract tests; native Functions/API/portal parity; all three real integration families; live Event Grid subscription validation and duplicate delivery; managed identity and deployment authorization; crash/lease-loss/replay tests; actual default and overridden retention cleanup with drift detection; bounded growth/health; deployment, upgrade/rollback and recovery instructions; and measured service costs. The storage-only lab and passing M5 tests do not close M6.

Current task status is maintained in the [master checklist](master-checklist.md). WBS 6.1 verification added six runtime ownership cases, three simulated renewal-failure/cancellation cases, two live Azure claim tests, and five passing API contracts against an isolated runtime build. Azure reported EUR 0.0000201 in the resource-group cost query on 8 October 2026; billing delay means this is not the final campaign cost.


## Historical preparation verification

Before deployment, the storage Bicep compiled locally without warnings/errors. The original two opt-in service probes compiled and were reported as **skipped** because no approved live endpoint was configured. The live results above supersede that preparation status. The Azure test project's existing Testcontainers dependency still brings SSH.NET security advisories; no Docker or broker software was installed for this preparation.

Backend review follow-up: `be7b6c2`. Portal review follow-up: `b6af335`. Both are local commits; neither was pushed during this work.
