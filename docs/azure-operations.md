# Azure operations and supported limits

The [native installer](../infra/azure-small/README.md) uses Functions plus Blob/Table/Queue, optionally Basic Service Bus. Shared execution semantics and management authentication match the filesystem API. SQL is a business-system connector, never the orchestration database.

## Retention and admission

Pipeline `ttlSeconds` defaults to 2,592,000 (30 days), range 1–31,536,000. Blob destinations inherit it or override it. Source-owned files are untouched. `maxExecutionSeconds` defaults to 86,400 and cannot exceed 604,800, including retries/resumes; expired work requires replay. YAML determines automatic/manual retry and uncertain-write handling.

| Resource | Small-profile limit |
|---|---|
| Execution document | 32 MiB encoded UTF-8 |
| State checkpoint | Less than 4 MiB |
| Shared retention ledger | 4 MiB serialized; 2,048 slots; 1 GiB reserved bytes |
| Registrations | 1,000/hour; 64 MiB artifact upload bytes/hour |
| Execution reservation | 12 MiB / three slots, including terminal and deletion checkpoints |
| Catalog | 4 MiB; 256 live pipeline IDs (tombstones excluded); 128 retained revisions per pipeline |
| Attempts | 100 per step across resumes |
| Recovery / cleanup | One bounded index page/minute; expiry/output cleanup every five minutes |
| Sync budget | 60 seconds in the template; keep below gateway deadlines |

Reservations are conservative capacity accounting, not Azure billable bytes. Capacity exhaustion rejects new work. Accepted work can save a terminal result and one deletion tombstone using reserved capacity. After using its emergency terminal slot, an execution cannot resume: reclaim capacity, review uncertain writes and explicitly replay. Successful external writes whose receipts cannot be saved remain uncertain. Queue notifications are disposable hints; indexed state survives lost notifications, poison hints and restarts.

The ledger is versioned and fails closed on corruption **or absence**. Explicit fresh-installation bootstrap is described in the installer; readiness stays unhealthy until a sweep has completed. Initialization/history/version/orphan journals reconcile interrupted transitions with record isolation and generation-conditional ownership. Restore a corrupt/missing ledger from a consistent backup; never replace it with `{}`. Cleanup rotates past failed/live entries, including when recording one object's error also fails. An unconfirmed document upload has a one-day cleanup obligation; only a confirmed receipt extends it to the recovery backstop (ordinary execution expiry remains shorter).

`host.json` defaults to four Service Bus calls and a Storage Queue batch size of four with a refill threshold of two (up to six queue messages in flight). `files-25m` overrides both to one, with no queue refill overlap, and gates execution work to one memory slot per process. Authenticated management GETs, liveness and readiness remain available while that slot is occupied. Busy execution HTTP requests return 503 with `Retry-After: 5`, rather than waiting indefinitely. Azure SDK transport failures follow the step retry policy; lease renewals retry within the existing ownership deadline.

The native 380-day backstop covers dedicated owned data containers only. Normal deletion follows the shorter pipeline/destination policy. Control/package noncurrent versions and snapshots expire after seven days; current definitions and cleanup instructions do not expire by age. Soft delete adds up to seven days and Azure lifecycle scheduling adds provider delay. Exact-TTL physical erasure is not promised. Uncommitted blocks follow provider cleanup. Source files must live outside the owned output container because lifecycle covers that whole container.

## Health, security and recovery

`/api/health` is liveness. `/api/readiness` verifies actual lifecycle, soft delete/version settings and managed-identity deletion of probe versions/snapshots. Azure requires `Elwood:Retention:Enforced=true`. Checks cache for at most five minutes. `Elwood:VerificationOnly=true` deliberately returns readiness 503 during verification.

Authenticated `/api/retention` exposes sweep time, reserved bytes/slots, overdue counts and bounded failures. Alert on failing readiness, overdue cleanup, record failures and sustained admission rejection. Cleanup more than 15 minutes overdue closes new-write admission. One inaccessible output remains recorded while others are removed. Preserve host cleanup target mappings through credential rotation and pipeline retirement.

Inspect attempts before recovering stuck work. Restore provider access; use resume only where eligible and after required uncertainty acknowledgement. Replay creates a new execution using an explicitly chosen original/active revision. Never use queue deletion to declare success. The RabbitMQ ingress readiness check covers storage, not broker connectivity; monitor its connection logs separately.

Runtime data roles are account-scoped, Service Bus access queue-scoped. Use a dedicated storage account and least-privilege database procedure grants. Test/Run secrets and connector grants are separate; exact Blob-prefix grants restrict business data. Internal containers are blocked as connector targets. HTTP destinations still require an installation network policy for untrusted authors. Management token roles are implemented; enterprise SSO is not.

## Logs, backups, upgrades and migration

The templates now include Application Insights, a 30-day Log Analytics workspace capped at 0.1 GB/day, and an alert for an unhealthy or missing retention heartbeat over 15 minutes. Pass existing action group IDs for notifications. The isolated worker emits bounded `ELWOOD_RETENTION_HEALTHY`/`ELWOOD_RETENTION_UNHEALTHY` messages without payloads or credentials; these worker signals are not sampled. Host telemetry retains its configured sampling. Approve the additional monitoring costs before provisioning; the cap is not a hard monetary limit. A stopped ingestion pipeline can hide evidence, so also monitor workspace limits and alert health. Exported diagnostic blobs need finite expiry. Real-test captures remain sensitive execution data.

### Catalog export and pruning

Azure stores drafts and published bundles **inside one mutable, ETag-protected `catalog.json`**. Revision hashes are verified when a changed catalog is read; these are not separate content-addressed immutable blobs. A conditional GET revalidates the cached active-route map on trigger calls and the batched RabbitMQ poll; unchanged catalogs are not downloaded, re-hashed or parsed again. Mutations remain compare-and-swap. The 4 MiB limit still applies, including tombstones, which preserve retired IDs but no longer consume the 256-live-pipeline quota.

An identity with pipeline-scoped `read` can call `GET /api/pipelines/{id}/export` to download format 1 JSON containing the draft, active revision and published bundles (including retired pipelines). Save the export to an access-controlled file with finite backup retention. An identity with `deploy` can call `POST /api/pipelines/{id}/revisions/prune`, with `If-Match: "<current-draft-hash>"` and a JSON array of revision hashes to remove. Export first; the active revision cannot be pruned. Unknown revisions/stale drafts reject the whole operation. Retired pipelines can have all revisions pruned; their small tombstones remain. Executions already accepted retain their independent captured bundle; pruning removes revision selection for future explicit replays. The filesystem provider currently returns 501 for these maintenance endpoints. Keep YAML/scripts in Git; an exported revision's `content` can be imported as a new draft through the normal authoring API.

LRS and seven-day soft delete/versioning are not cross-region disaster recovery. Keep pipeline bundles in Git. For additional backup, pause ingress and drain/pause workers, capture catalog, ledger, state/documents and Table indexes consistently, and give backup copies a finite independently budgeted expiry. Store secret configuration separately. Verify restoration in an isolated installation.

Before runtime upgrade, pause ingress/consumers/recovery and let calls finish. Interrupted writes retain uncertain checkpoints. Capture a consistent recovery set and prior application package, deploy, verify readiness, then resume. Do not roll code back across incompatible state schemas; restore matching code and consistent data, reviewing external effects since the backup.

This ledger format first ships with the native profile. Unreleased lab ledgers lacking `formatVersion` must be upgraded offline, preserving all entries and reserving 12 MiB/three slots per live execution. Mixed old/new writers are unsupported. Never reset a ledger to bypass migration.

Filesystem/legacy Redis stores do not transparently convert to Azure. Drain them, export YAML/scripts, create Azure drafts, validate/test/deploy, then redirect ingress with source identities protecting the cutover. Keep the old runtime until its work and cleanup obligations expire. Legacy records without expiry must not acquire a silently shortened retention. In-flight cross-provider migration is not certified.

## Cost and adoption sizing

West Europe retail samples checked 8 October 2026: Flex €0.000023/GB-second and €0.000004/ten executions; Service Bus Basic €0.043999/million operations. Illustratively, 20,000 GB-seconds plus 10,000 invocations is about €0.464 before storage, networking, tax or grants. This is a calculation, not a measured monthly forecast. [Functions pricing](https://azure.microsoft.com/en-us/pricing/details/functions/), [retail price API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices).

Always-running Linux B1 ingress was €0.015839/hour (~€11.56/730 hours), alone exceeding the pet project's €3–5/month incremental target. Deldo can use authenticated Event Grid/HTTP without it. The lab's SQL Basic (~€4.25/month), ACR Basic (~€4.40/month) and combined fixture host are verification costs, not required Elwood services. No separate portal-host charge is included when using an existing host or local portal.

**This guarded profile does not support the entire proposed Deldo envelope yet.** A 25 MiB CSV expanding to 50–75 MiB JSON exceeds the document limit. Thirty-day retention of 1,000 executions exceeds the 1 GiB reservation budget before payload copies. Actual supplier samples, peak/expanded sizes, throughput, ledger contention, footprint and Sweden Central pricing require a capacity/large-file package before adoption. Raising limits alone is insufficient without memory, cleanup, throughput and cost validation. Controlled fixtures establish behavior, not that workload or a €3–5 monthly guarantee.

[File-content suppression](content-deduplication.md) is implemented: default process, optional skip, 30-day configurable hash history and explicit replay override. Local/API, Azure SDK concurrency and hosted independent-Blob/replay verification pass.

The [files-25m capacity profile](capacity-profile.md) adds 4 GiB workers, bounded 128 MiB documents and 64 retention partitions. Three concurrent 25 MiB generated Deldo runs, duplicate skipping/replay, physical cleanup and a hosted 1,000-execution metadata model passed. This is a separate capacity profile; the default guarded limits above remain unchanged. Hosting-cost approval and actual supplier/database acceptance remain open.
