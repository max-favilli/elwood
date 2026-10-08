# File-content duplicate suppression

Requirement clarified by the maintainer on 8 October 2026 during M6 verification: an integration may receive identical file bytes under a new filename, Blob ETag or event ID. YAML must let its maintainer choose whether to process or skip that repeated content. The default remains **process**.

**Status: requirement/design, not an implemented YAML feature.** The older [architecture vision](architecture-vision.md) mentioned `CheckIdm` / `SaveIdm` with TTL. The current runtime instead implements retained incoming HTTP idempotency keys, RabbitMQ message IDs and Event Grid topic/event IDs. None detects a new event carrying the same file bytes. Deldo's database `(blob_path, etag)` import key also does not cover this case.

## Proposed contract

- Opt in on the source supplying the file. Use SHA-256 over the exact fetched bytes, before decoding, parsing or transformation; do not hash filenames, ETags or a reserialized JSON document.
- Scope fingerprints to installation + stable pipeline ID + source name. A newly deployed script does not silently erase that history. An explicit replay can force reprocessing; the operator must be able to see that choice.
- Process by default. In skip mode, a matching **successfully completed** integration can cause the new execution to finish as skipped, with the source, fingerprint, reason and original execution identity visible in history.
- Persist a claim before dependent processing. Concurrent copies must not both write destinations. An in-flight original is not a successful duplicate: the second copy waits or reports a retryable conflict rather than silently discarding work.
- Mark the fingerprint successful only after all required outputs have completed. Failed or uncertain executions retain their recovery ownership; they must not permanently suppress an integration that never finished. A replacement execution must not bypass unresolved uncertain writes.
- Retry/resume continues the original execution and fingerprint claim. An explicitly forced replay creates a new execution with an auditable override. Mock tests do not consult or mutate live fingerprint history; real tests need an explicit test-scoped policy.
- Hash history must have finite retention, bounded size/admission, independent cleanup and restart-safe ownership in filesystem and Azure stores. The confirmed default is 30 days, configurable per pipeline; explicit replay can force processing again (maintainer confirmation, 8 October 2026).
- Suppression avoids transformations and downstream writes after the source has been read. Detecting equal bytes still requires reading/hashing them unless a separately trusted provider checksum can prove their identity.

Do not publish a sample configuration until the parser, source byte-hash contract, durable claim store, execution statuses and portal inspection have a tested implementation. Missing/unsupported settings must fail validation rather than silently doing nothing.

## Acceptance checklist

- [x] Confirm retention and explicit replay policy: 30 days by default, configurable per pipeline, with an explicit replay override.
- [ ] Add strict YAML configuration and raw-byte fingerprint capture without changing DSL behavior.
- [ ] Implement atomic fingerprint ownership, successful completion and finite expiry in local and Azure stores.
- [ ] Distinguish skipped duplicates from successful processing in API/portal history.
- [ ] Cover same content/new name/new ETag, changed content/same name, concurrent arrivals, failed/uncertain original, crash/recovery, deployment changes, replay and retention expiry.
- [ ] Exercise Deldo with two independently uploaded identical files and verify one downstream import/snapshot set.
