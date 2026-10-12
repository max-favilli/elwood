# Pipeline YAML Reference

Elwood pipelines are defined in `.elwood.yaml` files. A pipeline describes **where data comes from** (sources), **how it's transformed** (maps and scripts), and **where it goes** (outputs and destinations).

The central concept is the **IDM (Intermediate Data Model)** — a shared JSON document built progressively by sources and consumed by outputs.

## Current implementation limits

Native Azure hosting is available through [the Azure installer](../infra/azure-small/README.md), with Blob/Table state and either Storage Queue or Service Bus **internal** scheduling. This does not add Service Bus source/destination YAML types. See [operations and capacity limits](azure-operations.md) and the [measured large-file profile](capacity-profile.md).

Optional root `deduplication: { mode: skip, source: file, ttlSeconds: 2592000 }` suppresses identical raw file bytes after a successful integration. The default mode is `process`. Choose one Blob/HTTP GET file pull source; other pulls must depend on it. History defaults to 30 days independently of execution history. Duplicate executions have status `Skipped`; unfinished owners cause a recoverable conflict. Real/mock draft tests do not read or publish live fingerprints. Explicit replay can override suppression. See [file-content suppression](content-deduplication.md) for the complete contract and recovery boundaries.

Pipeline `ttlSeconds` defaults to `2592000` (30 days), valid range `1`–`31536000`. It controls terminal execution-history retention and the default retention of delivered Blob objects. A `blobStorage` destination may override `ttlSeconds`; its clock starts at the write, independently of history expiry. Pipeline `maxExecutionSeconds` defaults to `86400`, maximum `604800`, and includes retries/manual resumes. An expired execution needs a new replay. Source-owned objects are never deleted by this policy. See [storage retention](storage-retention.md).

This reference includes planned capabilities. The runtime validates configuration instead of silently ignoring unsupported behavior. Unknown keys and unimplemented connector types are errors. Per-source/output retry policy, terminal retention and sync failure responses are implemented in M4; see [durable processing](durable-processing.md) for the supported schema and hosting boundaries. The documented destination aliases `restEndpoint` and `azureFileShare` map to `http` and `fileShare`; specifying both names for the same destination type is an error.

The current executors reject `join` and `outputId`, whose behavior is not implemented. Runtime source fan-out via `path` is also rejected; it remains available in the file-input CLI executor. Concurrency values remain scheduling hints; the reference durable runtime serializes steps within each execution and processes different executions concurrently. Runtime HTTP/Blob delivery supports JSON and raw string text/XML; SQL destinations accept JSON. CSV/binary/Parquet output serialization is not implemented; the default file destination supports literal local paths, not Azure connection strings or computed filenames. The filesystem Runtime API registers HTTP, SQL Server procedures and Azure Blob connectors, plus external RabbitMQ and Event Grid ingress. See [M5 connector contracts](connectors.md) for exact fields, defaults, access grants, limits and receipts. SFTP/Azure File Share, Service Bus ingress/delivery and schedules are not enabled in this host; Service Bus/email destination keys are not part of the current schema.

Script references are resolved for source maps/paths, HTTP request bodies, SQL parameter values, output paths/maps/IDs, response status codes and destination filenames. Resolving a script does not make an unsupported configuration executable. Evaluation errors and missing scripts fail their step; warnings retain the step's successful status when there is no error and are stored with the step's diagnostics.

```
Sources → IDM → Outputs → Destinations
```

---

## Quick Example

```yaml
version: 2
name: order-sync
description: Receive orders via HTTP, enrich with products, deliver to file share and API
mode: sync                    # see "Execution Mode" below

sources:
  - name: orders
    trigger: http
    endpoint: /api/orders
    contentType: json
    map: normalize-orders.elwood

  - name: products
    trigger: pull
    depends: orders
    path: $.orders[*]
    concurrency: 10
    from:
      http:
        url: https://api.example.com/products/{$.sku}
    map: enrich-order.elwood

outputs:
  - name: api-response
    path: $.orders[*] | where o => o.status == "active"
    map: format-output.elwood
    contentType: json
    response: true            # ← returned to the HTTP caller (required for sync mode)

  - name: file-archive
    path: $.orders[*] | where o => o.status == "active"
    contentType: json
    destinations:
      azureFileShare:
        - connectionString: $secrets.fileShare.connectionString
          filename: /exports/{$.orderId}.json
      restEndpoint:
        - url: https://erp.example.com/api/import
          method: POST
          headers:
            Authorization: Bearer $secrets.erp.token
```

---

## Top-Level Structure

```yaml
version: 2                    # Schema version (required)
name: my-pipeline             # Pipeline name (required)
description: What it does     # Human-readable description (optional)
mode: sync                    # Execution mode: "sync" (default) or "async"

sources: [...]                # Where data comes from (required, 1+)
outputs: [...]                # What data to produce and where to send it (required, 1+)
```

---

## Execution Mode

The `mode` field controls how the pipeline runs in the cloud runtime (Azure Functions / AWS Lambda / Kubernetes). It does **not** affect CLI execution — `elwood pipeline run` always runs end-to-end in-process.

### `mode: sync` (default)

The HTTP function runs the entire pipeline within its own invocation lifetime, then **returns one designated output** to the HTTP caller. This is the request-response pattern most integrations use.

```yaml
mode: sync   # explicit (default — the field can be omitted)

sources:
  - name: api-trigger
    trigger: http
    endpoint: /api/products/{sku}

outputs:
  - name: api-response
    map: build-response.elwood
    response: true            # ← exactly one output must set this
  - name: audit-log
    destinations:
      blob:
        - container: audit    # side effect — not returned to caller
```

**Rules:**
- Exactly **one** output must have `response: true`
- The pipeline runs within the installed host and gateway request deadlines
- The designated output's content is returned to the caller as the HTTP response body
- Other outputs (without `response: true`) execute as **side effects** — they run before the response is returned, so failures abort the request

### `mode: async`

After persisting the input, pinned revision and pending work, the runtime returns `202 Accepted` + `executionId`. Workers execute saved steps, and recovery replaces lost queue hints. An authorized management client can query `GET /api/executions/{id}`. Completion callbacks must be modeled explicitly as destinations; there is no automatic webhook subscription. Runtime source fan-out is not implemented.

```yaml
mode: async

sources:
  - name: bulk-orders
    trigger: http
    endpoint: /api/bulk-import

outputs:
  - name: write-warehouse
    destinations:
      restEndpoint:
        - url: https://wms.example.com/import
          method: POST
  # No "response: true" — async pipelines do not return data to the caller
```

**Rules:**
- **No** output may set `response: true` (validation error)
- The HTTP function returns `202 Accepted` with the execution ID
- Long-running and high-concurrency pipelines should use this mode
- State is queryable via the `Elwood.Runtime.Api` execution endpoints

### When to use which

| Use sync when... | Use async when... |
|---|---|
| Caller expects a response body | Caller fires-and-forgets (just acknowledges acceptance) |
| Total runtime fits inside the Function timeout | Total runtime may exceed Function timeout |
| Processing fits inside a single request deadline | Durable background processing is required |
| Errors should propagate to the caller as HTTP 4xx/5xx | Errors should be tracked in execution state for later retry |

You can convert a pipeline between modes by changing one line: `mode: sync` ↔ `mode: async` (and adding/removing `response: true` on the appropriate output).

---

---

## The IDM (Intermediate Data Model)

The IDM is the **single JSON document built by merging data from all sources**. It is the input to the output stage.

```
Source 1 (orders API)     → map writes $.orders
Source 2 (product catalog) → map writes $.products
Source 3 (warehouse stock) → map writes $.stock
    ↓
IDM = {
    orders: [...],     ← from source 1
    products: [...],   ← from source 2
    stock: [...]       ← from source 3
}
    ↓
Output 1: reads $.orders[*], transforms, delivers to file share
Output 2: reads $.orders[*], summarizes, delivers to API
```

Sources build the IDM. Outputs consume it. The IDM is never modified by outputs.

### How sources build the IDM

Each source map script receives:
- **`$`** — the raw payload from this source (the HTTP body, file content, queue message, etc.)
- **`$source`** — source metadata (trigger info, headers, event ID)
- **`$idm`** — the current IDM state (what previous sources have written)

The script returns data that is **merged into the IDM**.

```elwood
// normalize-orders.elwood — Source map for the "orders" source
// $ = raw HTTP payload, $idm = empty (first source), $source = HTTP metadata

return {
  orders: $.data.orderList[*] | select o => {
    id: o.orderId,
    sku: o.itemCode,
    customer: o.customerName,
    total: o.amount,
    status: o.orderStatus,
    correlationId: $source.http.headers["X-Correlation-Id"]
  }
}
// After this runs: IDM = { orders: [...] }
```

```elwood
// enrich-order.elwood — Source map for the "products" source (runs per order slice)
// $ = product API response for one order, $idm = current IDM with all orders

return {
  ...($idm),
  productName: $.name,
  productCategory: $.category
}
// Merged back into the IDM slice
```

---

## Sources

A source defines where input data comes from, how to transform it, and how it relates to other sources.

```yaml
sources:
  - name: orders              # Unique name (required)
    trigger: http             # Trigger type (required)
    endpoint: /api/orders     # HTTP path (for http trigger)
    contentType: json         # Payload format
    map: normalize.elwood     # Transformation script
    depends: other-source     # Execution dependency (optional)
    path: $.items[*]          # Fan-out: process once per slice (optional)
    concurrency: 10           # Parallel fan-out processing (optional)
```

### Trigger Types

| Trigger | Description | When it fires |
|---|---|---|
| `http` | HTTP endpoint | External system POSTs to the endpoint |
| `http-request` | Synchronous HTTP | Request-response: the output is returned to the caller |
| `queue` | Message broker | M5 Runtime API: external RabbitMQ with a `rabbitmq` block; native cloud broker ingress is M6 |
| `schedule` | Cron schedule | Runs on a timer |
| `pull` | Pull from external system | Pipeline fetches data from an API, file share, SFTP, SQL, etc. |
| `file` | File system watch | File appears in a monitored directory |

### Source Properties

| Property | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Unique identifier |
| `trigger` | string | yes | Trigger type |
| `endpoint` | string | for http | HTTP path. Supports expressions: `/api/{$.category}` |
| `contentType` | string | no | Payload format. Default: `json` |
| `map` | string | no | `.elwood` script or inline expression |
| `depends` | string or string[] | no | Source(s) that must complete before this one runs |
| `path` | string | no | Offline CLI source fan-out only; Runtime API and native hosts reject source `path` until distributed fan-out is implemented |
| `concurrency` | int | no | Offline CLI maximum parallel slices with `path`; default `1`. Does not enable runtime fan-out |
| `from` | object | for pull | Pull source configuration |
| `rabbitmq` | object | for live queue | External RabbitMQ subscription; async mode. See [connectors](connectors.md#external-rabbitmq-ingress). |
| `eventGrid` | object | no | Opt in to Event Grid schema validation, batch splitting and deduplication on async HTTP. See [connectors](connectors.md#event-grid-webhook-ingress). |

### Source Dependencies

Sources can depend on other sources. The executor resolves dependencies into a stage graph:

```yaml
sources:
  - name: orders              # Stage 1: no dependencies, runs first
    trigger: http

  - name: products            # Stage 2: needs orders data
    trigger: pull
    depends: orders
    from:
      http:
        url: https://api.example.com/products

  - name: warehouse           # Stage 2: also needs orders, runs CONCURRENTLY with products
    trigger: pull
    depends: orders
    from:
      http:
        url: https://api.example.com/stock

  - name: shipping            # Stage 3: needs BOTH products and warehouse
    trigger: pull
    depends: [products, warehouse]
    from:
      http:
        url: https://api.example.com/shipping
```

Execution graph:
```
orders
  ├──→ products   ──┐
  │                  ├──→ shipping ──→ IDM complete ──→ outputs
  └──→ warehouse  ──┘
```

Sources in the same stage (same dependency level) run concurrently. The IDM is only mutated between stages — never during concurrent execution.

### Source Fan-Out (path)

**Offline CLI only.** The Runtime API and native hosts reject source `path` fan-out, including local draft tests. The following describes the CLI's `$slice` behavior; it is not a deployable runtime example. M7 must implement distributed work-item coordination before the runtime accepts it.

When a source has `path`, the IDM is sliced and the source is processed **once per slice**:

```yaml
sources:
  - name: orders
    trigger: http
    map: extract-orders.elwood
    # After this: IDM = { orders: [{ sku: "A" }, { sku: "B" }, { sku: "C" }] }

  - name: product-details
    trigger: pull
    depends: orders
    path: $.orders[*]           # ← Fan-out: one API call per order
    concurrency: 10             # ← Up to 10 in parallel
    from:
      http:
        url: https://api.example.com/products/{$.sku}
    map: merge-product.elwood
```

With `path: $.orders[*]` and 3 orders, the product-details source runs 3 times — once per order. Each invocation receives one order slice as context. With `concurrency: 10`, up to 10 slices process in parallel.

### Pull Sources

When `trigger: pull`, the `from` section specifies exactly one connector. HTTP, SQL procedures and Blob are implemented in the Runtime API; the SFTP/Azure File Share examples below remain planned. SQL queries are not accepted. For authentication, SQL parameters/results, Blob version conditions and all defaults, use [M5 connector contracts](connectors.md).

```yaml
# REST API
from:
  http:
    url: https://api.example.com/data
    method: GET
    headers:
      Authorization: Bearer $secrets.api.token

# SFTP file
from:
  sftp:
    connectionString: $secrets.sftp.connectionString
    path: /exports/data.csv

# Azure File Share
from:
  azureFileShare:
    connectionString: $secrets.fileShare.connectionString
    path: /shared/data.json

# Azure Blob Storage
from:
  blobStorage:
    connectionString: $secrets.blob.connectionString
    container: input-data
    path: latest/data.json

# SQL database
from:
  sql:
    connectionString: $secrets.sql.connectionString
    storedProcedure: dbo.GetOrders
    readOnly: true
    parameters:
      status: { type: nvarchar, size: 20, value: '"pending"' }
```

---

## Outputs

An output defines what data to extract from the IDM, how to transform it, and where to deliver it.

```yaml
outputs:
  - name: active-orders
    path: $.orders[*] | where o => o.status == "active"
    map: format-for-erp.elwood
    contentType: json
    destinations:
      restEndpoint:
        - url: https://erp.example.com/api/orders
          method: POST
```

### Output Properties

| Property | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Unique name |
| `path` | string | no | Select data from the IDM once; the complete result becomes input to `map` |
| `outputId` | string | no | Reserved; currently rejected as unimplemented |
| `map` | string | no | Transformation: `.elwood` script or inline expression |
| `contentType` | string | no | Output format. Default: `json` |
| `concurrency` | int | no | Legacy field; does not enable per-item output processing or parallel delivery |
| `destinations` | object | no | Where to deliver (see Destinations) |
| `response` | bool | no | Sync mode only — marks this output as the HTTP response returned to the caller. Exactly one output must set this when `mode: sync`. Forbidden in async mode. See [Execution Mode](#execution-mode). |

### Output Selection (`path`)

Both runtime executors evaluate `path` once against the complete IDM, then evaluate `map` once against that whole result. An array stays an array; there is no per-item delivery loop. Use an explicit DSL `select` to transform its elements. Each configured destination receives the complete serialized output once per attempt (completed deliveries are checkpointed).

```yaml
path: $.orders[*] | where o => o.status == "active"
map: format-for-erp.elwood
```

### Context Bindings in Output Maps

| Binding | What it contains |
|---|---|
| `$` | The entire result of `path`, or the IDM if `path` is absent |
| `$idm` | The complete IDM (all mapped source data) |
| `$output` | The entire `path` result; bound only when `path` is configured |
| `$execution` | Runtime execution identity, step name and revision |

`$source` is not an output binding. Preserve required source metadata in the IDM during source mapping.

```elwood
// format-for-erp.elwood: $ is the full array of active orders.
let totalOrders = $output | count
return $ | select o => {
  OrderID: o.orderId,
  Customer: o.customer.toUpper(),
  Amount: o.total.toString(),
  BatchSize: totalOrders
}
```

### Output Processing Pipeline

```text
IDM
  → path (select once)
  → map (transform the whole selection once)
  → contentType (serialize JSON, or a string as text/XML)
  → destinations (each receives the complete output)
```

---

## Destinations

Destinations define where output data is delivered. An output can have multiple destinations (fan-out to multiple targets).

### Destination Types

The Runtime API implements these destinations:

| Type | Description |
|---|---|
| `http` / `restEndpoint` | HTTP request with JSON, text or XML body; Basic and OAuth2 client credentials |
| `sql` | SQL Server stored procedure; complete mapped JSON parameter and receipt |
| `blobStorage` | Azure Blob output; text/JSON/XML serialization and conditional writes |

Authored SOAP/XML uses an HTTP destination with a raw XML string. A trusted embedded/file host may install local file delivery (`fileShare` / `azureFileShare`, literal local filenames only); the Runtime API disables filesystem connectors. The name `azureFileShare` does not implement remote Azure File Share access.

**Planned, unavailable destination types:** `serviceBus`, `soap`, `emailCommService`, `servicePoint`, `request` and `sftp`. They have no implemented runtime destination connector. Depending on the key, parsing or execution validation rejects them. Use `response: true` for a sync HTTP response, not `request`. Do not put planned keys in deployable bundles.

### HTTP

```yaml
destinations:
  restEndpoint:
    - url: https://api.target.com/import
      method: POST
      headers:
        Authorization: Bearer $secrets.target.token
        Content-Type: application/json
```

### Blob Storage

```yaml
destinations:
  blobStorage:
    - connectionString: $secrets.blob.connectionString
      container: output-data
      filename: orders/{$.orderId}/{$execution.id}.json
      overwrite: false  # default: fail if the object exists
```

### SQL

```yaml
destinations:
  sql:
    - connectionString: $secrets.sql.connectionString
      storedProcedure: dbo.usp_ImportOrder
      parameter: payload  # entire mapped JSON as @payload nvarchar(max)
```

### Multiple Destinations

```yaml
destinations:
  restEndpoint:
    - url: https://erp.example.com/api/orders
      method: POST
  blobStorage:
    - connectionString: $secrets.blob.connectionString
      container: snapshots
      filename: archive/{$execution.id}.json
```

### Retry Policy

Configure `retry` on a source or output. A destination entry does not accept `retryPolicy`, `maxRetries` or `waitSeconds`. An output policy governs its incomplete destinations; checkpointed successful deliveries are skipped on resume. For example:

```yaml
outputs:
  - name: publish
    retry:
      mode: automatic
      maxAttempts: 3
      delayMs: 5000
      statusCodes: [429, 503]
      transportErrors: true
      allowUncertainWrites: false
    destinations:
      restEndpoint:
        - url: https://api.target.com/import
          method: POST
```

Use `mode: manual` when another attempt needs explicit approval. An uncertain write still requires operator review unless the maintainer opts into `allowUncertainWrites: true`. See [durable processing](durable-processing.md) for the full policy and acknowledgement contract.

Service Bus and email destinations are deferred; `serviceBus` and `emailCommService` are not current YAML schema keys and are rejected. Do not add them to a deployable bundle.

### Dynamic Expressions in Destinations

Supported dynamic fields such as HTTP URLs and Blob filenames accept inline Elwood expressions. See [connector configuration](connectors.md) for the supported fields:

```yaml
filename: /exports/{$.category}/{$.orderId}.json     # Data from the complete mapped output
url: https://api.example.com/orders/{$.orderId}      # Dynamic URL
```

In destination expressions:
- `$` is the complete mapped output (possibly an array)
- `$execution.id` is the runtime execution ID
- `$idm` is the full IDM
- Carry required source metadata into the IDM in the source map; outputs do not have a current `$source`.

---

## Source Metadata (`$source`)

Source maps receive `$source` metadata alongside the payload. Fields depend on the ingress adapter or CLI envelope; the following shapes illustrate the concepts, not fields guaranteed by every host. In particular, the offline CLI generates `payloadId` while the current runtime does not. See [offline CLI differences](local-testing.md#offline-cli-boundary) and [connector metadata](connectors.md).

```
$source.name             → "orders" (source name from YAML)
$source.trigger          → "http"
$source.eventId          → "evt-abc-123" (unique execution ID)
$source.payloadId        → "pay-def-456" (unique payload ID)
$source.timestamp        → "2026-03-25T10:30:00Z"
```

### HTTP trigger metadata
```
$source.http.method      → "POST"
$source.http.path        → "/api/orders"
$source.http.headers     → { "Content-Type": "...", "X-Correlation-Id": "..." }
$source.http.query       → { "category": "shoes" }
```

### Queue trigger metadata
```
$source.queue.name       → "orders-q1"
$source.queue.messageId  → "msg-123"
$source.queue.metadata   → { "priority": "high" }
```

---

## Secrets

Secrets use `$secrets` references. The executor loads them from a configured provider — secrets are never stored in the pipeline YAML.

```yaml
# In the pipeline YAML — just references
connectionString: $secrets.sql.connectionString
headers:
  Authorization: Bearer $secrets.api.token
```

### Secret Providers

The executor is configured (separately from the pipeline) with a secret provider:

| Provider | Description |
|---|---|
| Environment variables | `$secrets.x.y` → env var `ELWOOD_SECRET_X_Y` |
| Azure App Configuration | Reads from Azure App Configuration service |
| Azure Key Vault | Reads from Azure Key Vault |
| `.env` file | Reads from a local `.env` file (development) |

For the CLI executor, secrets default to environment variables or a `.env` file.

---

## Content Type Conversion

Sources and outputs specify `contentType` to handle format conversion automatically:

| contentType | Source (input) | Output (serialization) |
|---|---|---|
| `json` | Parsed as JSON | Pretty-printed JSON |
| `csv` | Parsed with `fromCsv()` | Serialized with `toCsv()` |
| `xml` | Parsed with `fromXml()` | Serialized with `toXml()` |
| `text` | Passed as string | Joined with `toText()` |
| `binary` | Base64-encoded | Base64 decoded to bytes |
| `xlsx` | Parsed with `fromXlsx()` | Serialized with `toXlsx()` (requires extension) |
| `parquet` | Parsed with `fromParquet()` | Serialized with `toParquet()` (requires extension) |

---

## Inline Expressions vs External Scripts

| Type | Syntax | When to use |
|---|---|---|
| **Static** | `contentType: json` | Fixed config values |
| **Inline expression** | `path: $.orders[*]` | Simple data access, short filters |
| **External script** | `map: transform.elwood` | Complex logic, let bindings, multiple pipes |

**Guideline:** inline for anything short. External `.elwood` files for anything with `let`, conditionals, or multi-pipe logic.

---

## Running Pipelines

### CLI Executor (development & testing)

```bash
# Single source
elwood pipeline run pipeline.elwood.yaml --source orders=payload.json

# Multi-source
elwood pipeline run pipeline.elwood.yaml \
  --source orders=orders-payload.json \
  --source products=products-data.json

# With output directory
elwood pipeline run pipeline.elwood.yaml \
  --source orders=payload.json \
  --output-dir ./output/

# Validate only
elwood pipeline validate pipeline.elwood.yaml
```

### Envelope Files

Source files can include metadata for testing scripts that use `$source`:

```json
{
  "source": {
    "name": "orders",
    "trigger": "http",
    "eventId": "evt-001",
    "http": {
      "method": "POST",
      "headers": { "X-Correlation-Id": "corr-789" }
    }
  },
  "payload": {
    "orders": [{ "id": "ORD-001", "customer": "Alice" }]
  }
}
```

Plain data files (no envelope) also work — `$source` gets minimal defaults.

---

## Complete Example

See the [sample pipeline](../spec/pipelines/sample-pipeline/) for a working example with YAML, scripts, and test data.
