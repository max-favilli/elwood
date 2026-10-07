# Storage retention: first-release requirement

Confirmed by the maintainer on 6 October 2026: **both internal execution data and delivered output blobs have a default retention of 30 days**, configurable per pipeline, with overrides for individual output destinations. Enforced cleanup is a first-release acceptance requirement for every supported hosting provider. Storage must not depend on an operator periodically deleting old files.

This is an M6 release gate, building on M4's local execution retention. Output retention and cloud provisioning described here are required work, not features already supplied by the M5 Blob connector. No storage-account-wide deletion policy has been applied by recording this requirement.

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

This does **not** yet delete blobs delivered through the M5 destination connector. The cloud document adapter also lacks an installed/verified lifecycle policy, and the local reference does not yet reconcile pre-state orphan documents or automatically migrate legacy records without expiry. Those gaps, output-policy configuration, cloud cleanup, lifecycle provisioning, quotas and observability are mandatory M6 work. Do not add a destination YAML flag that merely advertises retention without implementing and testing its enforcement.
