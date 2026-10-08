# M6 verification and external review handoff

Status: native Azure M6 package complete within the documented guarded-profile limits; ready for the owner's external review. Deldo capacity and content deduplication remain adoption prerequisites. Baselines: backend `655c359`, portal `b6af335`. Cloud work is restricted to temporary `elwood-m6-rg`, West Europe, with a €10 campaign allowance. No workplace production system was used.

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

Backend non-integration suites: Core 374, Pipeline 214, CLI 22, Parquet 7, Connectors 21, Azure 19 and Hosting 11 (668 total). Five isolated API contracts pass. The final Azure subset passed 32 tests with one actual-policy test initially skipped for its missing resource-ID opt-in; that actual-policy test was then explicitly enabled and passed, alongside the generation regression. The subset includes unit checks: do not describe all 32 as live service tests. Separate Service Bus transport, hosted recovery, families, retention, native queue and portal browser runs supply their own live evidence.

Portal: 120 Node checks (plus the opt-in browser test skipped in the ordinary suite), eight component tests, production build, and lint with zero errors/three existing hook warnings. Production npm audit and the full solution's transitive NuGet vulnerability audit are clean. Developer-only npm findings are described above. Remote CI and the owner's external AI review are not claimed.

An 8 October campaign snapshot listed **241,415,419 bytes (230.23 MiB)** across current/version/deleted Blob records: packages 197,387,855; catalog/control 40,901,386; state 2,931,469; documents 158,639; other containers 36,070 bytes. This inventory includes version/soft-delete records but is not a storage invoice. The rolling metric snapshot showed **31,177 storage transactions**, **491 Functions executions**, **366,899,200 MB-milliseconds = 358.3 GB-seconds**, and zero always-ready usage. At the sampled retail compute/execution rates that is about **€0.00844**, excluding all other meters and free grants. [Metric conversion](https://learn.microsoft.com/en-us/azure/azure-functions/functions-consumption-costs).

Cost Management still reported only **€0.0000265452**, plainly delayed and not the total campaign cost. The separately billed fixture B1 + SQL Basic + ACR Basic amount to about €0.67/day at sampled rates, plus builds/operations. The short campaign remains comfortably within the approved €10 based on provisioned duration and observed usage; it does not establish Deldo's monthly budget. Final test activity adds a small amount beyond this snapshot.

The generic `runtime.bicep` passed what-if and deployment against the already provisioned lab, and readiness returned 200 with verification-only disabled. The final package SHA-256 is `3805e7dca4534ee94ad6f750dcd1ddc8ff4ad693898efacba0d0984c8f514c40`. One earlier ZIP command uploaded successfully but its health check failed because the app was stopped (CLI reported an SSL EOF); starting it and checking the deployment/real readiness/hosted smoke resolved this. The final deployment command completed successfully; readiness returned 200, hosted execution/duplicate acceptance/pinned replay passed, and the final portal browser test passed in 19.5 seconds. The exact temporary group was checked by tags and inventory, deleted, and `az group exists` returned `false`. Local API and portal were restarted and both returned HTTP 200.
