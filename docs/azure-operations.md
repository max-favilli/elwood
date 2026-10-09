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
| Catalog | 4 MiB; 256 stable pipeline IDs; 128 revisions per pipeline |
| Attempts | 100 per step across resumes |
| Recovery / cleanup | One bounded index page/minute; expiry/output cleanup every five minutes |
| Sync budget | 60 seconds in the template; keep below gateway deadlines |

Reservations are conservative capacity accounting, not Azure billable bytes. Capacity exhaustion rejects new work. Accepted work can save a terminal result and one deletion tombstone using reserved capacity. After using its emergency terminal slot, an execution cannot resume: reclaim capacity, review uncertain writes and explicitly replay. Successful external writes whose receipts cannot be saved remain uncertain. Queue notifications are disposable hints; indexed state survives lost notifications, poison hints and restarts.

The ledger is versioned and fails closed on corruption. Initialization/history/version/orphan journals reconcile interrupted transitions with record isolation and generation-conditional ownership. Restore a corrupt ledger from a consistent backup; never replace it with `{}`. Cleanup rotates past failed/live entries.

The native 380-day backstop covers dedicated owned data containers only. Normal deletion follows the shorter pipeline/destination policy. Control/package noncurrent versions and snapshots expire after seven days; current definitions and cleanup instructions do not expire by age. Soft delete adds up to seven days and Azure lifecycle scheduling adds provider delay. Exact-TTL physical erasure is not promised. Uncommitted blocks follow provider cleanup. Source files must live outside the owned output container because lifecycle covers that whole container.

## Health, security and recovery

`/api/health` is liveness. `/api/readiness` verifies actual lifecycle, soft delete/version settings and managed-identity deletion of probe versions/snapshots. Azure requires `Elwood:Retention:Enforced=true`. Checks cache for at most five minutes. `Elwood:VerificationOnly=true` deliberately returns readiness 503 during verification.

Authenticated `/api/retention` exposes sweep time, reserved bytes/slots, overdue counts and bounded failures. Alert on failing readiness, overdue cleanup, record failures and sustained admission rejection. Cleanup more than 15 minutes overdue closes new-write admission. One inaccessible output remains recorded while others are removed. Preserve host cleanup target mappings through credential rotation and pipeline retirement.

Inspect attempts before recovering stuck work. Restore provider access; use resume only where eligible and after required uncertainty acknowledgement. Replay creates a new execution using an explicitly chosen original/active revision. Never use queue deletion to declare success. The RabbitMQ ingress readiness check covers storage, not broker connectivity; monitor its connection logs separately.

Runtime data roles are account-scoped, Service Bus access queue-scoped. Use a dedicated storage account and least-privilege database procedure grants. Test/Run secrets and connector grants are separate; exact Blob-prefix grants restrict business data. Internal containers are blocked as connector targets. HTTP destinations still require an installation network policy for untrusted authors. Management token roles are implemented; enterprise SSO is not.

## Logs, backups, upgrades and migration

Templates create no paid logging workspace/export sink. Logs omit payloads/credentials. If enabling filesystem logs, configure finite retention/quota. Before adding Application Insights or Log Analytics, approve sampling, daily cap, finite retention and cost. Exported diagnostic blobs also need finite expiry. Real-test captures remain sensitive execution data.

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
