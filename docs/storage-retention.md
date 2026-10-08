# Storage retention: first-release requirement

Confirmed by the maintainer on 6 October 2026: **both internal execution data and delivered output blobs have a default retention of 30 days**, configurable per pipeline, with overrides for individual output destinations. Enforced cleanup is a first-release acceptance requirement for every supported hosting provider. Storage must not depend on an operator periodically deleting old files.

This is an M6 release gate, building on M4's local execution retention. The M6 implementation now supplies output ownership, cleanup ledgers and a verified Azure lifecycle backstop. The bounded native Azure milestone is verified in [the release evidence](m6-release-evidence.md); [the master checklist](master-checklist.md) tracks external review and remaining adoption requirements.

## Ownership and effective policy

| Data | Default and boundary |
|---|---|
| Accepted input copies, intermediate documents, source responses, IDM, output copies, receipts, attempts, traces and execution-specific revision snapshots | 30 days after the execution reaches a terminal state; retain the recovery set consistently |
| Objects delivered to a Blob/S3/GCS destination, including real-system test deliveries | 30 days from the object/version write; inherit the pipeline policy unless the destination overrides it |
| Objects merely read from a source | Not owned by Elwood; never delete them as part of execution or output retention |
| Drafts and active/pinned published pipeline bundles | Definition lifecycle, not disposable execution payloads; retain while referenced |
| Orphaned uploads, staging data and incomplete writes | Finite cleanup policy, with ownership checks and protection for active writers |
| Previous versions, snapshots and soft-deleted data created by Elwood | Explicit bounded cleanup/retention as part of the deployment's storage policy |

Configuration must use positive finite durations. Omitting a value means 30 days, never unlimited. A pipeline default applies to its outputs; a destination can choose a different finite duration. The effective policy is bound to the deployed revision/object write. Saving a draft does not change live deletion behavior. Existing data must not acquire a shorter retention through an implicit migration; an explicit inventory/migration procedure is required.

Execution and destination expiry have different clocks. A destination can expire independently of a longer execution-history window; its receipt must identify the effective expiry so operators understand what remains available. Conversely, a destination retained longer than the history window still needs a durable cleanup record after the execution state disappears. Never keep cleanup instructions only in an execution document that expires first.

Updates to an existing object must preserve ownership and version identity. Cleanup must not delete a newer replacement or another application's object at the same path. Retries, replay and uncertain write outcomes need tests for this. Restrict management to Elwood-owned containers/buckets or explicit owned prefixes/objects; do not install a blanket rule over unrelated application storage. No retention of source files is implied by permission to read them.

The pet project's indefinitely retained raw supplier files are **source-owned input**, outside this policy. Its normalized snapshots are **Elwood-delivered outputs**, subject to the new 30-day default or an explicit finite destination override. This supersedes any assumption that Elwood snapshots are kept forever.

## Enforcement required in M6

1. Persist effective expiry and ownership for every execution artifact and managed output write, including uncertain/partially completed writes. Avoid a write-to-cleanup-registration gap that can orphan an object indefinitely.
2. Run an idempotent cleanup service independently of pipeline workers, using bounded expiry queries and resumable progress. One failed deletion must not block all other cleanup. Deleting a pipeline must not abandon cleanup obligations for its retained data.
3. Install and verify provider-native lifecycle safeguards through deployment automation. Where an application-controlled expiry is needed for precise per-pipeline semantics, lifecycle rules must complement it without deleting active recovery data prematurely. A bare “delete 30 days after creation” rule over active execution documents is insufficient.
4. Cover current objects, previous/noncurrent versions, snapshots, delete markers, temporary uploads and finite soft-delete retention. Include provider delays and soft-delete windows in the storage/cost calculation; “eligible for deletion” does not mean bytes disappear at that instant.
5. Detect drift: missing lifecycle policies, revoked deletion permissions, blocked immutable objects and failed cleanup. Surface last successful sweep, expired backlog/age, deletion failures and retained bytes/counts, with actionable alerts. Deployment/readiness checks must not report retention as enforced when its required policy or permissions are missing.
6. Define finite execution/retry lifetimes and storage admission limits. Protect active executions from ordinary TTL cleanup, but do not allow abandoned work or an unavailable cleanup service to cause silent unlimited growth. This needs explicit timeout/backpressure behavior, not deletion underneath a running worker.
7. Include indexes, idempotency records, execution-specific snapshots and temporary control records in cleanup. Retain only the definition versions and recovery documents still needed. Backups, application logs and diagnostics need their own documented finite policies.

For Azure, deleting a base blob alone is insufficient when versions/snapshots or soft delete are enabled; the deployment must configure those policies too. The service's lifecycle and soft-delete behavior must be accounted for in final reclamation. [Azure lifecycle deletion](https://learn.microsoft.com/en-us/azure/storage/blobs/lifecycle-management-policy-delete), [Azure soft delete](https://learn.microsoft.com/en-ie/azure/storage/blobs/soft-delete-blob-overview).

For S3, include noncurrent version expiration, expired delete-marker cleanup and incomplete multipart-upload cleanup where applicable. [S3 lifecycle actions](https://docs.aws.amazon.com/AmazonS3/latest/userguide/intro-lifecycle-rules.html).

For GCS, include noncurrent versions and the bucket's soft-delete retention rather than assuming deletion of a live object immediately frees its storage. [GCS lifecycle](https://docs.cloud.google.com/storage/docs/lifecycle), [GCS soft delete](https://docs.cloud.google.com/storage/docs/soft-delete).

These are provider requirements, not a claim that AWS/GCP hosting adapters already exist. Every provider advertised as supported must pass the same retention acceptance contract before release.

## Required acceptance evidence

- Default 30-day expiry and pipeline/destination overrides work for both internal and output objects. Invalid/unlimited settings are rejected. Receipts/history expose the effective policy and expiry.
- Advanced test clocks expire data without waiting 30 days; provider integration tests verify actual removal within the documented service delay/window.
- Versioned/overwritten blobs, snapshots, incomplete uploads, failed executions, failed acceptance and real-system test outputs do not leak indefinitely.
- Restart during deletion resumes safely. One inaccessible object does not starve others. Revoked permissions and missing/drifted policies create a visible failed health/alert state.
- Outputs outliving their execution history are still cleaned. Deleting a pipeline or rotating credentials does not lose the cleanup obligation. Source files and unrelated or newer destination objects survive.
- Active work, resumed work and replay-owned recovery copies are protected. Hung work and retention backlog reach a finite operational limit instead of silently growing forever.
- Azure low-cost hosting includes retained versions, soft-deleted bytes, scans and cleanup operations in the measured budget.

## Current implementation and work remaining

The local runtime already defaults `ttlSeconds` to 2,592,000 for internal execution state/documents. It writes `expiresAt` on completion, failure or awaiting review and performs restartable deletion under an execution claim. The cleanup worker now runs independently of `Elwood:Durable:WorkerEnabled` and recovery failures, on startup and every minute. Individual deletion failures retain their tombstone, allow other executions to be cleaned, and are reported for retry. Tests cover paused processing, partial cleanup failure/retry and active-data protection.

The Blob destination now requires an independent retention service. Before uploading, it reserves capacity and persists a unique ownership ID, credential-free target identity and conservative expiry. The write carries that ID in metadata; its receipt adds the actual version, ETag, retention seconds and expiry. A lost response remains discoverable through ownership metadata. Cleanup removes only owned objects/versions and preserves unrelated source data and newer replacements. Credentials come from host-owned `Elwood:Retention:BlobTargets`, independently of pipeline grants and history; retain these target mappings through credential rotation and pipeline retirement.

`ttlSeconds` defaults to 2,592,000 at pipeline level and is inherited by Blob destinations; an individual Blob destination can override it. Values must be 1–31,536,000 seconds (365 days). `maxExecutionSeconds` defaults to one day and is bounded to seven days from original acceptance, including retries and manual resumes. An expired execution can be replayed into a new execution but cannot be resumed indefinitely. A step has at most 100 attempts across resumes. Ordinary TTL pruning still protects active executions; recovery settles lifetime expiry, preserving uncertain-write review semantics.

The Azure small profile registers documents and checkpoint generations before upload and reserves 12 MiB / three slots per execution, including emergency terminal and deletion checkpoints. Its shared CAS ledger admits at most 2,048 slots / 1 GiB of reserved artifact bytes, 1,000 registrations/hour and 64 MiB of uploaded artifact bytes/hour. The ledger itself is bounded to 4 MiB. Capacity exhaustion or cleanup over 15 minutes overdue stops additional writes while reserved capacity still permits accepted work to terminate and be pruned. An execution using its emergency terminal slot requires explicit replay after capacity is reclaimed; resume is disabled. Generation-conditional cleanup cannot remove a concurrent acceptance's renewed reservation. Reserved bytes are conservative capacity accounting, **not** Azure billable bytes. Soft-deleted data, old control versions and logs require separate accounting. Cleanup runs independently every five minutes in Functions and every minute locally, processing at most 100 obligations per sweep. `/api/retention` reports sweep time, registered bytes/slots, overdue objects and bounded failures.

The Azure profile installs a 380-day last-resort lifecycle over the dedicated state/document/output containers. This is later than the seven-day execution limit plus the maximum 365-day history/output policy and writer grace. It supplements precise ledger expiry; it does not replace the 30-day default. Control/package noncurrent versions and snapshots have a seven-day policy; current definitions and cleanup instructions do not expire by age. Blob/container soft delete is bounded to seven days. Azure's uncommitted-block cleanup is provider-owned; completed late uploads remain covered by the ownership journal and native backstop. The policy verifier reads the actual Azure resource settings and probes write/snapshot/version deletion rights; cloud admission fails when these checks fail. Permission/policy checks are cached for at most five minutes.

Verified on 8 October 2026: actual output/version/snapshot deletion, lost-response reconciliation, replacement/source isolation, journal restart, failed-permission isolation and restored cleanup, history-GC reconciliation, quota exhaustion after a write and successful pruning, and a deployed timer removing real-test/async outputs after history expired. Actual lifecycle settings and version-deletion probes pass. See [release evidence](m6-release-evidence.md) and [operations](azure-operations.md) for final status, guarded-profile capacity limits and the explicit legacy migration procedure.
