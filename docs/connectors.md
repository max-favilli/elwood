# M5 connector contracts

The filesystem Runtime API supports HTTP, SQL Server procedures, Azure Blob Storage, external RabbitMQ queues and Event Grid webhooks. These connectors use the shared execution engine, revision snapshots, diagnostics, checkpoints and [retry/recovery policies](durable-processing.md). Native cloud hosting and cloud state/queue adapters remain M6. Adding a connector does not make the legacy Azure Functions host support the new deployment catalog or recovery contract.

Start with the [three sample bundles](examples/connectors/README.md). In the portal use **New Pipeline → Start from**, then **Test draft → Connectors → Mocks**. Real-system tests require your own endpoints and scoped credentials. A supplied trigger replaces the external message in local tests; testing a draft never subscribes to a broker or creates an Event Grid subscription.

## HTTP and the SAP interface

Both HTTP pull sources and destinations accept `user`/`password` for Basic authentication, or an `oauth2` object for client credentials. They cannot use both. For example, within `from.http`:

```yaml
url: $secrets.crm.url
method: POST
body: '{ orderId: $idm.order.id }'
connectionTimeout: 30000 # milliseconds, covers token acquisition and the call
oauth2:
  tokenUrl: $secrets.crm.tokenUrl
  clientId: $secrets.crm.clientId
  clientSecret: $secrets.crm.clientSecret
  scope: orders.write                 # optional
  clientAuthentication: basic         # basic (default) or body
```

`oauth2` has the same shape on each `destinations.http` entry. Token endpoints require HTTPS, except loopback HTTP for controlled tests. Successful tokens with a usable `expires_in` are cached until shortly before expiry; see authentication lifetimes below. Interactive authorization-code flows are not supported. Default clients do not follow redirects. An unsuccessful token request fails before sending the business request and is not an uncertain business write. Token exchanges and tokens are excluded from traces; authorization headers are redacted.

Sources can send raw string bodies with `bodyFormat: text` or `xml`; default `json` serializes the evaluated value. Outputs use `contentType: text` or `xml` and their map must return a string. HTTP headers can specify `Content-Type` and `SOAPAction`. This supports authored XML envelopes over HTTP, not WSDL discovery, automatic SOAP object mapping or TQL. The confirmed M5 SAP PI/PO flow is JSON HTTP with Basic authentication. Response bodies from destinations are retained as receipts. To use a response in later transformations, model the request as a dependent HTTP pull source, as in the CRM sample.

## SQL Server procedures

`Elwood.Pipeline.Sql` is optional for embedders; the Runtime API registers it. Only stored procedures are supported, using `CommandType.StoredProcedure` and typed parameters, never SQL text interpolation.

```yaml
- name: product
  trigger: pull
  depends: event
  from:
    sql:
      connectionString: $secrets.products.sql
      storedProcedure: dbo.GetProduct
      readOnly: true
      commandTimeoutSeconds: 30
      maxRows: 10000
      maxResultBytes: 10485760
      parameters:
        id: { type: int, value: '$idm.event.productId' }
        category: { type: nvarchar, size: 100, value: '"tyres"' }
  map: '{ product: $[0], event: $idm.event }'
```

Parameter `value` is a DSL expression or `.elwood` reference, evaluated with the assembled IDM as `$` and `$idm`. Names may include a leading `@`; identifiers must be simple names. Supported types: `nvarchar` (default), `varchar`, `int`, `bigint`, `bit`, `decimal`, `float`, `datetime2`, `datetimeoffset`, `uniqueidentifier`. String size defaults to `-1` (MAX); decimal precision/scale default to 18/2. Explicit null binds `DBNull`. Use literal one-part or schema-qualified procedure identifiers.

The first result set becomes a JSON array, preserving column names and nulls; duplicate column names are rejected. Later result sets are drained so completion/errors are observed, but are not returned. `$source.sql` contains `storedProcedure`, `rowCount` and `recordsAffected` (null when unknown). A read with no rows yields `[]`. Declare `readOnly: true` only for procedures known to be side-effect free; the default is false because a source procedure can write.

An SQL destination binds the **entire mapped JSON document** as one `nvarchar(max)` parameter:

```yaml
destinations:
  sql:
    - connectionString: $secrets.catalog.connectionString
      storedProcedure: catalog.usp_ImportOffers
      parameter: payload # optional, default; binds @payload
      commandTimeoutSeconds: 30
```

Its receipt is `{ "rows": [...], "sql": { "storedProcedure": "...", "rowCount": 1, "recordsAffected": null } }`. The same `maxRows` and `maxResultBytes` limits apply. Defaults are 10,000 rows, 10 MiB and 30 seconds. Configurable ceilings are 1,000,000 rows, 100 MiB and 3,600 seconds. Limits apply to returned data; an output payload can be larger. Connection opening and result reading share the operation deadline.

No command retries occur inside the connector. A timeout/transport fault is eligible for the authored policy. After a potentially writing command has started, **any failed result is conservatively uncertain**: a procedure may commit before a later statement raises an error, including a deadlock or nested procedure failure. A result-size failure after a write is also uncertain. Pre-send configuration/connection failures are not uncertain. An application-specific idempotency/transaction contract is the maintainer's responsibility; automatic retries of uncertain writes require `allowUncertainWrites: true`. Non-transient database errors are not automatically retried.

Inspection records procedure, parameter names/types, elapsed time, result rows and receipt; connection strings and parameter values are omitted from call metadata. Returned data and procedure error messages are still application data, subject to normal execution access controls and retention.

## Azure Blob reads and writes

`Elwood.Pipeline.Azure` supplies Blob connectors. Choose exactly one credential form: a scoped `connectionString` reference, or an `accountUrl` using a host-authorized Azure identity. Containers must already exist; the connector does not create them.

```yaml
- name: file
  trigger: pull
  depends: event
  contentType: text
  from:
    blobStorage:
      connectionString: $secrets.inbound.connectionString
      container: inbound
      path: '{$idm.event.blobPath}'
      ifMatch: '{$idm.event.etag}'
      # versionId: '{$idm.event.versionId}' # only when actually supplied by the event
      maxBytes: 26214400
      timeoutSeconds: 60
  map: normalize.elwood
```

Reads accept UTF-8 `text`, `csv`, `xml` (DSL strings) or `json`. Default limit: 10 MiB; configurable up to 100 MiB. Default timeout: 60 seconds, maximum 3,600. Invalid UTF-8 or oversized content fails. `$source.blob` contains `etag`, `versionId`, `contentLength`, `contentType`, `lastModified` and a URL with credential query values redacted.

`ifMatch` accepts quoted or unquoted ETags. HTTP 412 is a visible, non-transient version mismatch: Elwood never silently substitutes newer bytes. `versionId` selects an existing retained version; `ifMatch` alone does not discover old versions after replacement. A standard BlobCreated event may not supply a version ID. With only its ETag, a replaced object fails and needs operator handling or an upstream version-aware event contract.

```yaml
destinations:
  blobStorage:
    - connectionString: $secrets.inbound.connectionString
      container: analytics
      filename: 'deldo/{$idm.import.fileName}.{$idm.import.etag}.json'
      overwrite: true
      timeoutSeconds: 60
```

`filename` supports data/secret interpolation or a `.elwood` script, with `$` as the mapped output and `$idm` as the complete source IDM. Default `overwrite: false` means create-only (`If-None-Match: *`). Explicit `overwrite: true` allows replacement; `ifMatch` instead requires a particular existing ETag. Receipts contain `url`, `etag`, `versionId` and byte `size`. JSON and raw string text/XML delivery are supported; binary/Parquet serialization is not implemented.

SDK retries are disabled. Lost connections, deadlines and server failures after an upload starts may mean the write completed; the pipeline policy controls retry or review. A create-only conflict or stale ETag is not automatically retried. For deterministic version-named snapshots, use explicit `overwrite: true` with `allowUncertainWrites: true` only if rewriting the same payload is acceptable.

### Output retention release requirement

The maintainer requires delivered blobs to expire after 30 days by default, with pipeline defaults and per-destination overrides. The M5 connector does not yet enforce destination expiry; `ttlSeconds` currently covers execution artifacts only. M6 must provide durable output cleanup and verified lifecycle policies, including versions/snapshots and finite soft-delete retention. Source blobs are excluded. See [storage retention](storage-retention.md) for the release gate; do not assume an existing M5 upload is automatically deleted after 30 days.

## Scoped access to host identities

Named connection strings/passwords use the existing [Test/Run secret grants](management-and-deployment.md#secret-and-filesystem-boundaries). `secrets.json` uses flat keys such as `"products.sql"`, not nested objects. Identity-based access needs an additional host-owned allowlist:

```json
{
  "Elwood": {
    "ConnectionGrants": {
      "my-pipeline": {
        "Test": {
          "SqlIdentityServers": ["my-server.database.windows.net"],
          "BlobAccounts": ["https://my-account.blob.core.windows.net"]
        },
        "Run": {
          "SqlIdentityServers": ["my-server.database.windows.net"],
          "BlobAccounts": ["https://my-account.blob.core.windows.net"]
        }
      }
    }
  }
}
```

SQL Integrated Security and explicit `Authentication` modes require an exact `Data Source` grant. SQL database-file attachment is disabled. Blob `accountUrl` requires an exact HTTPS account root grant, with no query/userinfo/fragment. Azure RBAC and SQL permissions still apply. Grants authorize a server/account, not individual procedures/containers: use restricted service identities for narrower access. YAML cannot grant itself host identity access. Mocks resolve neither secrets nor identities.

## External RabbitMQ ingress

```yaml
- name: event
  trigger: queue
  rabbitmq:
    connectionString: $secrets.products.rabbitmq # amqps://... or local amqp://...
    queue: product-changes
    prefetch: 1
    maxMessageBytes: 1048576
    requireMessageId: true
  map: '{ event: $ }'
```

Requires async mode and an existing queue. The Runtime API starts/stops consumers from active deployments and reconnects after failures (five-second delay); saving a draft does not start one. Queue declaration, binding and producer message durability belong to the external system. The maximum prefetch is 1,000; message size can be raised to 10 MiB. JSON is the default; text-like payloads become UTF-8 strings.

The consumer commits trigger input, pinned revision and recoverable scheduling state **before ACK**. The internal Elwood work queue remains separate. A stable producer `MessageId`, combined with broker/vhost/queue/source/pipeline identity, deduplicates redelivery during retained execution history. A reused ID with different data is rejected. `$source.rabbitmq` includes message ID, exchange, routing key, redelivery flag, content type and queue.

Missing IDs, invalid payloads, oversized messages and storage failures are not silently discarded: they remain unacknowledged and are requeued on reconnect. Operators must correct or quarantine poison messages at the broker. Setting `requireMessageId: false` permits ID-less messages but forfeits deduplication for those messages. Broker acknowledgements do not guarantee exactly-once external writes; destination idempotency and retry policies still matter.

## Event Grid webhook ingress

Enable `eventGrid` explicitly on an async HTTP source:

```yaml
trigger: http
endpoint: /imports/deldo
eventGrid:
  maxEventsPerBatch: 100
  maxRequestBytes: 1048576
  topic: /subscriptions/.../providers/Microsoft.Storage/storageAccounts/...
  subjectPrefix: /blobServices/default/containers/inbound/blobs/deldo/
```

This adapter handles **Event Grid schema**, not CloudEvents or a Service Bus wrapper. Configure the source's HTTP authentication and matching Event Grid delivery credentials before exposing it. Authentication applies to validation and notifications. Topic/subject filters add routing checks, not cryptographic authentication. The sample omits deployment credentials so it can be tested locally; add authentication before a real deployment.

With `aeg-event-type: SubscriptionValidation`, a valid single validation event returns `{ "validationResponse": "<code>" }` and creates no execution. With `aeg-event-type: Notification`, the adapter validates the whole batch, then durably accepts **one execution per event**, each receiving a one-element array. HTTP 202 is returned only after all events are committed. Invalid shapes/filter mismatches return 400 and oversized bodies return 413. Limits can be raised to 5,000 events and 10 MiB.

Identity is derived from topic plus event ID, scoped to the pipeline/source. Partial storage failures may leave part of a batch accepted; redelivery reuses those execution identities instead of starting them again. Live Azure subscription delivery and native hosting are M6 verification, separate from these local webhook contract tests.

## Mocks, inspection and verification

SQL/Blob fixtures extend the existing [local testing contract](local-testing.md). SQL source `body` is a row array; Blob text source `body` is a string. `metadata.sql` and `metadata.blob` become `$source` metadata. SQL output fixture `body` supplies receipt rows; Blob output fixture `body` supplies its receipt object. Optional `error`, `retryable` and `outcomeUncertain` model SQL/Blob failures. `expectBody` checks the full outgoing JSON or raw string.

For an output with several connector types, fixture/delivery order is **HTTP, Blob, SQL**, then list order within each type; YAML mapping key order does not change it. Prefer separate outputs when explicit step ordering matters. Successful destination receipts and checkpoints survive resume; receipts do not implicitly mutate the IDM. The portal's connector-call details display operation, timing, request metadata and result/receipt for SQL/Blob, plus the existing HTTP headers and bodies.

Verification commands from the backend root:

```powershell
dotnet test dotnet/Elwood.slnx --filter "Category!=Integration"
dotnet build dotnet/Elwood.slnx
node --test scripts/runtime-api.test.mjs scripts/durable-runtime.test.mjs scripts/connectors-api.test.mjs
dotnet test dotnet/tests/Elwood.Connectors.Tests --filter "Category=Integration"
node --test scripts/rabbitmq-runtime.test.mjs
```

Integration tests require SQL Server, Azurite and RabbitMQ. `ELWOOD_TEST_SQL`, `ELWOOD_TEST_BLOB` and `ELWOOD_TEST_RABBITMQ` override their connections. Defaults are Windows LocalDB, default Azurite ports and localhost guest RabbitMQ. Tests create and delete uniquely named test databases/containers/queues; use dedicated test services. CI provides all three services. To run only locally available tests without RabbitMQ, filter `FullyQualifiedName!~RabbitMqIntegrationTests&Broker!=RabbitMq`.

The hosted RabbitMQ test additionally requires the management plugin. Set `ELWOOD_TEST_RABBITMQ_MANAGEMENT` to its HTTP base URL (default `http://127.0.0.1:15672`); credentials default to the AMQP URI’s credentials, or may be specified in the management URL. The test creates a unique queue, closes only that queue’s consumer connection, kills/restarts its own isolated Runtime API process, and deletes its queue/state afterward. It verifies reconnect, durable acceptance, revision pinning and redelivery deduplication with real acknowledgements. Build the Runtime API in Debug first. CI provisions the management-enabled broker and runs both integration commands.

The supplied-input CLI accepts and validates retry policies but does not execute them or connector delivery; it prints a note. A production bundle can therefore be used directly for offline transformation checks without removing retry blocks. The Deldo callable-lambda, numeric-conversion, deliberate-failure and strict-access requests are tracked as separate DSL work; M5 changes no language behavior.

Permanently invalid RabbitMQ messages (size limit, required MessageId, invalid UTF-8/JSON, or conflicting MessageId/payload) are rejected without requeue. Configure a dead-letter exchange/queue on the external queue to retain these rejects; without one RabbitMQ discards them. Logs identify MessageId, exchange, routing key and reason, without message bodies. Transient acceptance failures remain unacknowledged and reconnect with exponential delay bounded at 60 seconds, reset after successful acceptance.


## Cancellation and authentication lifetimes

Source and destination connector methods accept an optional caller `CancellationToken`. Host shutdown propagates it through HTTP requests (including OAuth2), Blob transfers, SQL connections/commands and file I/O, combined with each operation's configured deadline. A durable attempt interrupted during a possible write retains its pre-call checkpoint and enters the normal uncertain-write recovery policy on restart. Cancellation is not proof of rollback. Embedders implementing custom connectors must accept and honor the new parameter.

Blob connectors reuse a lazily created `DefaultAzureCredential` unless an explicit credential is supplied; pipeline/account grants are still checked on every operation. OAuth2 client-credentials tokens are cached in memory per HTTP client and exact resolved OAuth configuration, including the client secret and scope. Rotation uses a different hashed key. Only successful responses with a positive numeric `expires_in` are cached; expiry includes refresh headroom, and each client cache is bounded to 256 entries. Token exchanges and cached credentials remain excluded from traces and persistence.
