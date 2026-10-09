# File-content duplicate suppression

Requirement clarified by the maintainer on 8 October 2026 during M6 verification: an integration may receive identical file bytes under a new filename, Blob ETag or event ID. YAML must let its maintainer choose whether to process or skip that repeated content. The default remains **process**.

**Status: implemented and verified locally and on native Azure.** Independently uploaded 25 MiB Blob files with identical bytes produced one output set; the second execution was skipped, and explicit forced replay delivered again. This complements retained HTTP idempotency keys, RabbitMQ message IDs and Event Grid topic/event IDs. Deldo's database `(blob_path, etag)` import key does not detect identical bytes under a different object name.

## Configuration and behavior

```yaml
deduplication:
  mode: skip                  # omitted/default: process
  source: file                # primary file pull source
  ttlSeconds: 2592000          # successful fingerprint history: 30 days
```

The source must use Blob Storage, HTTP GET, or a local file connector in an embedded runtime. The hosted API still rejects local file connectors. Other pull sources must depend, directly or transitively, on this source. One primary file gates the integration; trigger JSON, SQL results and composites of multiple files are not supported fingerprint inputs. Validation rejects unsupported configurations. The offline file-input CLI rejects skip mode because it has no durable shared fingerprint history.

For Deldo, add the block above beside `mode: async`; keep `file` dependent on the event trigger. Hashing does not replace ETag/version conditions on the source read. For HTTP, bytes are the response content exposed by HttpClient (after any configured transport decompression), before text decoding. BOMs, whitespace and line endings are significant.

- Opt in on the source supplying the file. Use SHA-256 over the exact fetched bytes, before decoding, parsing or transformation; do not hash filenames, ETags or a reserialized JSON document.
- Scope fingerprints to installation + stable pipeline ID + source name. A newly deployed script does not silently erase that history. An explicit replay can force reprocessing; the operator must be able to see that choice.
- Process by default. In skip mode, a matching **successfully completed** integration can cause the new execution to finish as skipped, with the source, fingerprint, reason and original execution identity visible in history.
- Persist claim intent and an atomic fingerprint claim before transformation/dependent processing. An unfinished owner causes `AwaitingReview` with its execution ID (HTTP 409 for sync runs). Resume the duplicate after resolving the owner; it then skips if the owner succeeded. This is a recoverable conflict, not an automatic polling loop.
- Mark the fingerprint successful only after all required outputs have completed. Failed or uncertain executions retain their recovery ownership; they must not permanently suppress an integration that never finished. A replacement execution must not bypass unresolved uncertain writes.
- Retry/resume continues the original execution and fingerprint claim. `POST /api/executions/{id}/replay` accepts `forceProcessing: true`; the portal exposes a separate checkbox. It fences the original against further resume before starting a new, auditable execution. Uncertain writes still require their separate acknowledgement. A duplicate cannot override another execution's unfinished write: replay that owning execution instead.
- Both real and mock draft tests deliberately bypass live suppression and never reserve/publish live fingerprints. Their policy is execution-local testing, so repeated tests remain runnable. Real tests still perform the selected real external calls.
- Successful history defaults to 30 days from completion, configurable from 1 second to 365 days. Pending claims last at most execution lifetime plus that TTL. When failed/uncertain execution history expires, its deletion tombstone prevents resume and releases its own unfinished claim; expired recovery records cannot permanently block later deliveries. This does not resolve remote uncertain writes: after the recovery window, downstream business idempotency remains the integration owner's responsibility.
- Filesystem and Azure stores retain at most 8,192 fingerprints / 2 MiB per installation. Admission fails visibly when full; neither quota nor corrupt storage is treated as an empty history. Independent cleanup prunes expired fingerprints. Azure uses a bounded control Blob with ETag compare-and-swap; filesystem writes use a cross-process lock and atomic replacement. Runtime upgrades must pause old workers, since older releases do not honor this policy or the `Skipped` status.
- Suppression avoids transformations and downstream writes after the source has been read. Detecting equal bytes still requires reading/hashing them unless a separately trusted provider checksum can prove their identity.

`Skipped` is a separate terminal state (numeric API value 6). History records the source, SHA-256 and owning execution. Remaining sources/outputs are skipped. Synchronous duplicate responses are HTTP 200 with `{ executionId, skipped: true, duplicateOf }`; normal authored response data is not fabricated. Successful downstream completion is checkpointed before the fingerprint is marked completed; recovery can settle the final state without redelivering outputs.

## Acceptance checklist

- [x] Confirm retention and explicit replay policy: 30 days by default, configurable per pipeline, with an explicit replay override.
- [x] Add strict YAML configuration and raw-byte fingerprint capture without changing DSL behavior.
- [x] Implement atomic fingerprint ownership, successful completion and finite expiry in local and Azure stores.
- [x] Distinguish skipped duplicates from successful processing in API/portal history.
- [x] Cover equal bytes/new name/new ETag, changed bytes, concurrent arrivals, failed/uncertain original, recovery, revision changes, replay and retention expiry. Pipeline regressions, cross-instance filesystem tests, Azure SDK CAS conflict test, HTTP API restart/replay/real-test isolation and portal component tests pass.
- [x] Exercise the Deldo normalization script with two independently uploaded identical files and verify one downstream payload/snapshot set, followed by explicit replay. The capacity harness uses Blob sinks for both payloads; SQL behavior remains covered by the earlier connector campaign and real project acceptance.
