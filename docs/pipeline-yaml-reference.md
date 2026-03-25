# Pipeline YAML Reference

Elwood pipelines are defined in `.elwood.yaml` files. A pipeline describes **where data comes from** (sources), **how it's transformed** (maps and scripts), and **where it goes** (outputs and destinations).

The central concept is the **IDM (Intermediate Data Model)** — a shared JSON document built progressively by sources and consumed by outputs.

```
Sources → IDM → Outputs → Destinations
```

---

## Quick Example

```yaml
version: 2
name: order-sync
description: Receive orders via HTTP, enrich with products, deliver to file share and API

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
  - name: active-orders
    path: $.orders[*] | where o => o.status == "active"
    map: format-output.elwood
    contentType: json
    concurrency: 50
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

sources: [...]                # Where data comes from (required, 1+)
outputs: [...]                # What data to produce and where to send it (required, 1+)
```

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
| `queue` | Message broker | Message arrives on a queue (ASB, RabbitMQ, etc.) |
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
| `path` | string | no | Fan-out: slice the IDM, process this source once per slice |
| `concurrency` | int | no | Max parallel slices when `path` is set. Default: `1` |
| `from` | object | for pull | Pull source configuration |

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

When `trigger: pull`, the `from` section specifies where to get data:

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
    query: SELECT * FROM orders WHERE status = 'pending'
```

---

## Outputs

An output defines what data to extract from the IDM, how to transform it, and where to deliver it.

```yaml
outputs:
  - name: active-orders
    path: $.orders[*] | where o => o.status == "active"
    outputId: $.orderId
    map: format-for-erp.elwood
    contentType: json
    concurrency: 50
    destinations:
      restEndpoint:
        - url: https://erp.example.com/api/orders
          method: POST
```

### Output Properties

| Property | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Unique name |
| `path` | string | no | Fan-out: slice the IDM, process once per slice |
| `outputId` | string | no | Expression or script generating a unique ID per item |
| `map` | string | no | Transformation: `.elwood` script or inline expression |
| `contentType` | string | no | Output format. Default: `json` |
| `concurrency` | int | no | Max parallel items. Default: `1` |
| `destinations` | object | no | Where to deliver (see Destinations) |

### Output Fan-Out (path)

The `path` property slices the IDM. The output is processed **once per slice**:

```yaml
path: $.orders[*]                                     # One output per order
path: $.orders[*] | where o => o.status == "active"   # One output per active order
path: filter-orders.elwood                            # Script that returns the slices
```

### Context Bindings in Output Maps

When an output map runs, these bindings are available:

| Binding | What it contains |
|---|---|
| `$` | The current slice (one item from the fan-out) |
| `$idm` | The complete IDM (all source data) |
| `$output` | The full array selected by `path` (all slices, not just current) |
| `$source` | Source metadata from the triggering source |

```elwood
// format-for-erp.elwood — Output map
// $ = one active order (the current slice)
// $idm = full IDM with all orders, products, etc.
// $output = all active orders (the full filtered array)

let totalOrders = $output | count

return {
  OrderID: $.orderId,
  Customer: $.customer.toUpper(),
  Amount: $.total.toString(),
  BatchSize: totalOrders,
  Category: $idm.products[*] | first p => p.sku == $.sku | select p => p.category
}
```

### Output Processing Pipeline

```
IDM
  → path (fan-out: slice the IDM into items)
  → map (transform each item)
  → contentType (serialize: json, csv, xml, etc.)
  → destinations (deliver each item)
```

---

## Destinations

Destinations define where output data is delivered. An output can have multiple destinations (fan-out to multiple targets).

### Destination Types

| Type | Description |
|---|---|
| `restEndpoint` | REST API call (POST, PUT, PATCH) |
| `azureFileShare` | Azure File Share |
| `sftp` | SFTP file delivery |
| `blobStorage` | Azure Blob Storage |
| `serviceBus` | Azure Service Bus queue/topic |
| `sql` | SQL database (stored procedure) |
| `soap` | SOAP web service |
| `emailCommService` | Azure Email Communication Services |
| `servicePoint` | HTTP endpoint for pull retrieval |
| `request` | Response body for http-request trigger (sync flow) |

### REST API

```yaml
destinations:
  restEndpoint:
    - url: https://api.target.com/import
      method: POST
      headers:
        Authorization: Bearer $secrets.target.token
        Content-Type: application/json
```

### Azure File Share

```yaml
destinations:
  azureFileShare:
    - connectionString: $secrets.fileShare.connectionString
      filename: /exports/{$.orderId}.json
```

### SFTP

```yaml
destinations:
  sftp:
    - connectionString: $secrets.sftp.connectionString
      filename: /incoming/{$.code}.xml
```

### Blob Storage

```yaml
destinations:
  blobStorage:
    - connectionString: $secrets.blob.connectionString
      container: output-data
      filename: orders/{$.orderId}/{$source.eventId}.json
```

### Service Bus

```yaml
destinations:
  serviceBus:
    - connectionString: $secrets.asb.connectionString
      queue: order-notifications
```

### SQL

```yaml
destinations:
  sql:
    - connectionString: $secrets.sql.connectionString
      storedProcedure: usp_ImportOrder
```

### Email

```yaml
destinations:
  emailCommService:
    - connectionString: $secrets.email.connectionString
      to: notifications@example.com
      subject: Order {$.orderId} processed
```

### Multiple Destinations

```yaml
destinations:
  restEndpoint:
    - url: https://erp.example.com/api/orders
      method: POST
  azureFileShare:
    - connectionString: $secrets.fs.connectionString
      filename: /archive/{$.orderId}.json
  serviceBus:
    - connectionString: $secrets.asb.connectionString
      queue: order-events
```

### Retry Policy

Destinations support retry policies:

```yaml
destinations:
  restEndpoint:
    - url: https://api.target.com/import
      method: POST
      retryPolicy:
        maxRetries: 3
        waitSeconds: 5
```

### Dynamic Expressions in Destinations

All string properties in destinations support inline Elwood expressions:

```yaml
filename: /exports/{$.category}/{$.orderId}.json     # Data from current slice
url: https://api.example.com/orders/{$.orderId}      # Dynamic URL
subject: Order {$.orderId} - {$.status}              # Dynamic email subject
```

In destination expressions:
- `$` is the current output slice
- `$source` is the source metadata
- `$idm` is the full IDM

---

## Source Metadata (`$source`)

Every source provides metadata alongside the payload:

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
