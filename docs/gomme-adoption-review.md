# Deldo adoption review and M5/M6 alignment

Reviewed 6 October 2026 against the local Elwood implementation and `D:\git-repos\gommenew`. This document can be passed back to the pet project's AI session. It records an integration review and implementation plan; it does not announce shipped connectors or authorize a deployment.

## Decision

The original HTTP order and RabbitMQ/SQL product integrations **and** the Deldo supplier-import flow are joint M5 priorities. Shared connector infrastructure comes first, followed by SQL in both directions, Blob reads/writes, ingress/authentication and acceptance across all flows. Deldo does not displace the original scope.

Native Azure deployment remains M6. Include an installation without mandatory Redis and evaluate its cost within the pet project's **less than €15/month total application budget**. The workload, region and rest of the application's bill are needed before judging feasibility.

## What was verified

| Check | Result |
|---|---|
| Original target `pipelines/deldo-stock/pipeline.elwood.yaml` | Current CLI rejects the missing `PullSourceConfig.blobStorage` property. Parsing stops there; this is not a complete list of unsupported fields. SQL destinations are also absent from the schema. |
| `pipeline.localtest.elwood.yaml`, supplying both source files | Current .NET CLI completes: 6 rows, 4 offers, 2 rejected. Both output JSON files match the checked-in expected file set and contents. |
| Real connectors | Not exercised by that offline CLI run. Supplied source files bypass network reads and the CLI writes outputs locally. This proves transformations and output contracts, not SQL/Blob delivery. |
| Ordinary `let isUsable = r => ...` | Fails in both the current .NET CLI and built TypeScript engine, at the lambda binding. |
| Same predicate bound with `memo` | Succeeds in both engines and produces the expected rejected row in a two-row reproduction. |
| SQL scripts | Reviewed statically. The pet session's LocalDB results were not rerun and no database was modified during this review. Concurrency and ordering observations below are code-review findings, not reproduced database failures. |

No files in the pet repository were changed. Elwood's current changes remain local and uncommitted. M1–M4 are the reference runtime foundation; M5 connectors and M6 installation remain to be implemented.

## Disposition of the six requests

| Request in `docs/elwood-requests.md` | Owner | Contract and evidence required |
|---|---|---|
| 1. Blob pull source | M5 | Optional Azure package; connection string or host-authorized identity; container/path resolution; explicit text decoding; JSON support; metadata; missing-object diagnostics; ETag/version-aware reads; strict mocks and controlled Blob tests. |
| 2. SQL stored-procedure destination | M5, shared with SQL pull work | Whole mapped output as one parameterized `nvarchar(max)` input; proposed default parameter name `payload`; first result-set receipt; timeout and cancellation; database error details; uncertain-write classification; secret redaction. Test against SQL Server as well as fake command boundaries. |
| 3. Blob destination | M5 | Existing YAML name plus documented identity/overwrite/precondition settings; JSON snapshot serialization; destination path resolution; receipt with object identity, ETag and size; recoverable partial delivery. More output formats require actual serializers. |
| 4. Event Grid ingress | M5 protocol support, M6 live wiring | Prefer the explicitly enabled webhook path for the first Deldo acceptance flow. Validate the subscription handshake separately from execution, authorize notifications, handle batches without loss, deduplicate each event and acknowledge only durable acceptance. Service Bus delivery is an alternative requiring its own envelope test and hosting setup. |
| 5. Minimal Azure profile | M6 | Evaluate Functions + Table/Blob/Queue services without weakening M4 recovery and ownership. Provide deployment automation, operational tests and a workload-based cost estimate. Keep a Service Bus option distinct from external RabbitMQ ingestion. |
| 6. Callable ordinary lambda | Separate DSL follow-up | Confirmed current limitation, not a pipeline runtime regression. Keep the present Deldo script valid; consider ordinary closures in a separate change covering both engines, scope/capture, diagnostics and regression behavior. `memo` caches results and is not a general promise of uncached function semantics. |

Field names for new options are proposals until implemented and documented. In particular, adding connector classes alone is insufficient: the current runtime and inspection contracts assume HTTP headers/statuses and infer write risk from HTTP methods. M5 must make those contracts suitable for SQL and Blob while preserving HTTP behavior.

SQL pull needs typed parameter binding and a defined representation of result rows. SQL destination needs a receipt even when the command writes data and returns a table. A receipt is available for inspection; automatic reuse of terminal destination receipts as downstream source data is not implied. Use a dependent source step for calls whose response is required by the next transformation.

## Feedback for the pet project's bundle

### 1. Use the procedure's actual schema

The YAML names `usp_ImportOffers`, but `sql/002-usp_ImportOffers.sql` creates `catalog.usp_ImportOffers`. Use the schema-qualified name in the adoption bundle. Depending on the database principal's default schema is unnecessary and can resolve incorrectly or fail.

### 2. Bind a blob event to the bytes it describes

The event contains an ETag, but the proposed pull source reads only `container + path`. If a supplier replaces that path before a delayed event is processed, the pipeline can read new bytes while recording the old ETag. That invalidates the import's deduplication identity.

M5 should support an ETag-conditioned download that fails visibly on a mismatch, plus explicit version selection where available. An ETag precondition detects replacement; it does not retrieve an old version. The application must retain immutable uploads or addressable versions if every version must remain recoverable before Elwood has captured the file. Azure's [Get Blob contract](https://learn.microsoft.com/en-us/rest/api/storageservices/get-blob) supports conditional reads and version addressing.

Persist the fetched payload and metadata for resume. Define separately whether a fresh replay should repeat external reads or require the original version. Include account/container identity, blob path and version/ETag in the application import key; make the version identity required for this flow rather than allowing missing ETags to disable deduplication.

### 3. Do not silently discard additional events

`event.elwood` starts with `$[0]`. That is correct only for an enforced one-event envelope. Event Grid defaults to one event per delivery but can batch events; delivery order is not guaranteed. Its batch acknowledgement is all-or-none. The ingress adapter must either normalize each event into a separate durable execution or explicitly reject unsupported batch sizes. A success response must never acknowledge events the pipeline ignores. See Microsoft's [delivery and retry contract](https://learn.microsoft.com/en-us/azure/event-grid/delivery-and-retry).

For split batches, deduplicate by a namespaced event identity and make partial persistence safe on redelivery: already accepted members reuse their executions, remaining members are accepted, and the batch succeeds only after all members are durable. A local two-event test is required. Choose and document the normalized source envelope before assuming the same `$[0]` script works for both HTTP and Service Bus.

The Event Grid subscription-validation request must return its validation code without starting an integration. Enable this behavior only on a configured Event Grid route; the presence of a header alone must not bypass authentication. CloudEvents uses a different validation protocol and is not implicitly supported by implementing the Event Grid schema handshake. See [webhook endpoint validation](https://learn.microsoft.com/en-us/azure/event-grid/end-point-validation-event-grid-events-schema).

Also validate event type, container and supplier path. Replacing a prefix in a string and using strict property access does not prove that the prefix was present or that the event was `BlobCreated`.

### 4. Test duplicate imports concurrently

The SQL procedure checks `IF EXISTS` and later inserts, while a unique index protects `(blob_path, etag)`. With ordinary concurrent execution, two transactions can both pass the check; uniqueness prevents duplicate loaded records, but one caller can receive a constraint error instead of the intended `duplicate` receipt. Sequential redelivery tests do not cover this race.

Choose a database-side concurrency strategy and test simultaneous calls. Cover concurrent product upserts too, including different imports containing the same EAN. Return a consistent receipt after a duplicate is recognized. Elwood cannot make an application's check-then-insert logic atomic across a database and external services.

### 5. Define what "current" means for late events

`catalog.current_offers` chooses `MAX(import_id)` for each supplier/feed. A delayed older upload imported later can therefore replace newer prices in the current view; `last_seen_at` can also move backwards. Choose the business ordering rule, retain the necessary source version/order metadata, and test newer-then-older delivery. An opaque ETag identifies a version; it is not a chronological sort key. Keep this policy in the catalog domain rather than making Elwood guess which prices are current.

### 6. Make snapshots identify an import version

`deldo/{receivedDate}/{fileName}.json` collides when the same filename is replaced twice on one day. Use a safe, deterministic version component and feed identity if the goal is one retained snapshot per import version. Define whether a redelivery rewrites identical content or reports an existing snapshot, and test conflicting content. Avoid using a fresh execution ID alone as the import identity: a replay has a different execution ID.

### 7. Finish validation at the shared contract boundary

The current script rejects missing EAN, price and quantity. Nonempty invalid numbers, negative values, malformed EANs and fractional quantities need an explicit policy. The JSON schema allows a numeric quantity while SQL reads it as `int`; agree whether stock is integral. Verify season behavior for unknown supplier codes.

The presence of a schema file does not automatically validate pipeline outputs. Add fixtures that validate the produced JSON against it, and decide which bad rows are retained as rejected versus which fail the whole import. The real supplier CSV and column mapping remain adoption inputs.

### 8. Author retry policies and harden the test runner

The target bundle currently declares no retry policies. Select automatic versus operator-authorized recovery per step using the M4 YAML contract. A SQL timeout after transmission can mean the procedure committed; automatic retry is appropriate only when the maintainer has chosen it with the database's duplicate behavior understood. If SQL succeeds and the snapshot fails, resume should reuse the completed SQL result. Fresh replay intentionally revisits earlier operations and still needs application idempotency.

`pipelines/test.ps1` currently creates missing expected files even without `-Update` and iterates only the outputs produced. A missing output or absent baseline should fail verification, with fixture updates confined to explicit update mode. The review independently compared both file sets and parsed JSON, so this weakness did not mask its result.

## M5 acceptance evidence

Maintain separate acceptance rows for HTTP orders, RabbitMQ/SQL products and Deldo imports. All must pass before M5 is complete.

- New connectors validate their real schema and expose strict mock fixtures; mock mode cannot reach networks or secrets.
- Controlled real tests cover Blob text reads and version mismatch, SQL source parameters/results, SQL destination receipts, Blob uploads, and RabbitMQ acknowledgement/redelivery.
- Deldo produces the expected offer/rejection contract and a distinct versioned snapshot. Repeated and concurrent delivery exercises the application's duplicate contract.
- Crash/timeout tests cover SQL writes of uncertain outcome and failure between destinations. Resume reuses completed results; fresh replay remains distinguishable and revision-pinned.
- Event Grid tests cover handshake, invalid/unauthorized input, two-event delivery and partial durable acceptance followed by redelivery.
- Portal inspection presents SQL procedure/result metadata, Blob object/version metadata and message identity with timing, errors, warnings and redacted credentials, alongside existing HTTP details.

## M6 evidence and remaining inputs

The small Azure profile must pass the same recovery contract as the reference runtime. A Table adapter needs conditional state transitions and recoverable indexes; a lease-based worker needs ownership enforcement and stale-worker handling across external calls. Queue visibility alone does not establish exclusive execution or exactly-once remote effects. Use bounded recovery queries, payload/trace documents and consistent retention cleanup.

Specify file size, uploads/day, concurrency, expected latency, region, source-version retention and the rest of the application's hosting costs. Include the portal, management API, SQL, storage, event delivery, secrets, monitoring and transfer in the budget calculation. No Azure resources or paid services were created as part of this review.

Event Grid transport retries and Elwood step retries are separate. The former concern handing an event to Elwood; the latter begin after Elwood durably owns an execution. Do not rely on a blanket "all non-2xx responses retry for 24 hours" rule: Event Grid has configurable attempt/TTL limits and status-specific behavior, with dead-lettering requiring configuration. See the [official retry policy](https://learn.microsoft.com/en-us/azure/event-grid/delivery-and-retry).

## M5 follow-up — 6 October 2026

The connector schema is now implemented locally; see [connector contracts](connectors.md). The current pet-project target YAML validates without its HTTP twin. A copied synthetic bundle is available in the portal as **Deldo supplier import** and is verified through mocks and real LocalDB/Azurite. The updated fixture has 11 rows, five offers and six rejections. The real integration test captures the SQL receipt, fails a missing snapshot container, then resumes with one SQL call total. These results supersede the earlier six-row offline sample counts; this review's earlier findings remain historical context.

Adoption details for the other session:

- `from.blobStorage` supports `ifMatch`, `versionId`, `maxBytes` and `timeoutSeconds`. Set `maxBytes: 26214400` for the stated 25 MB input ceiling (default is 10 MiB). A stale ETag fails; an old version is read only when its actual `versionId` is supplied. Enabling versioning alone does not make an ETag an address for old bytes.
- SQL destinations bind all mapped JSON to `@payload` by default; `parameter: payload` is accepted. First-result rows are captured in a receipt. Use `catalog.usp_ImportOffers` and keep the procedure's application-level deduplication. Any error after a potentially writing procedure starts is conservatively uncertain, because earlier statements may already have committed.
- Add **`overwrite: true`** to the version-named snapshot destination if redelivery should rewrite identical content. The connector defaults to create-only; `allowUncertainWrites` alone does not change that storage condition. The copied Elwood sample includes both settings.
- Event Grid schema ingress performs the handshake, splits a batch into one-element arrays, filters topic/subject when configured, and durably deduplicates by topic/event ID. Source HTTP authentication applies to the handshake and notification. Add deployment authentication; the local example has none. Service Bus delivery is an M6 alternative and its envelope is not yet verified.
- Test/Run secret grants and optional host-identity grants must be configured by the runtime operator. Real local tests use a supplied trigger and perform actual SQL/Blob calls; mock tests need no secrets.
- CLI file-input runs accept valid retry policy and print that connector retries are not applied offline. The target bundle can replace the retry-stripped twin after this code is adopted. This session has not changed any files in `gommenew`.

The pet project's newer requests about callable lambdas, strict numeric conversion, `fail(message)` and consistent strict access are recorded as separate DSL follow-ups (requests 6–9). Their claims have not been fully reproduced in this connector milestone, and changing production language semantics requires its own compatibility/parity review. Its updated region, size, frequency, concurrency and cost assumptions are recorded for M6; a €3–5/month incremental budget has not been proved.

### Retention clarification after M5

The maintainer confirmed that Elwood-delivered snapshots must default to **30 days** and support a pipeline default plus a per-destination override. Do not assume the `analytics` snapshots live forever. Raw inbound supplier files remain source-owned and may keep the pet project's separate long-term policy. Enforced output retention/lifecycle provisioning is an M6 release gate; the current M5 connector does not yet expire uploaded blobs. See [storage retention](storage-retention.md). This session has not altered the pet project's storage policies or files.
