# Pipeline YAML Reference

Elwood pipelines are defined in `.elwood.yaml` files. A pipeline describes **where data comes from** (sources), **how it's transformed** (maps and scripts), and **where it goes** (outputs and destinations).

---

## Quick Example

```yaml
version: 2
name: order-sync
description: Receive orders via HTTP, transform, send to file share

sources:
  - name: orders
    trigger: http
    endpoint: /api/orders
    contentType: json
    map: normalize-orders.elwood

outputs:
  - name: active-orders
    path: $.orders[*] | where o => o.status == "active"
    map: format-output.elwood
    contentType: json
    destinations:
      fileShare:
        - connectionString: ${FS_CONNECTION}
          filename: /exports/active-orders-{$source.eventId}.json
```

This pipeline:
1. Receives JSON orders via an HTTP POST endpoint
2. Runs `normalize-orders.elwood` to transform the raw payload
3. Filters for active orders using an inline Elwood expression
4. Transforms each active order through `format-output.elwood`
5. Writes the result to a file share

---

## Top-Level Structure

```yaml
version: 2                    # Schema version (required)
name: my-pipeline             # Pipeline name (required)
description: What it does     # Human-readable description (optional)

sources: [...]                # Where data comes from (required, 1+)
join: { ... }                 # How multiple sources are combined (optional)
outputs: [...]                # What data to produce and where to send it (required, 1+)
```

---

## Sources

A source defines where input data comes from and how to initially transform it.

```yaml
sources:
  - name: orders              # Unique name for this source (required)
    trigger: http             # How this source is activated (required)
    endpoint: /api/orders     # HTTP path (for http trigger)
    contentType: json         # Payload format: json, csv, xml, text, binary, xlsx, parquet
    map: normalize.elwood     # Transformation script (optional)
```

### Trigger Types

| Trigger | Description | When it fires |
|---|---|---|
| `http` | HTTP endpoint | Third-party system sends a POST to the endpoint |
| `queue` | Message broker | Message arrives on a queue (Azure Service Bus, RabbitMQ, etc.) |
| `schedule` | Cron schedule | Runs on a timer (e.g., every hour, daily at 3am) |
| `pull` | Pull from external system | Pipeline pulls data from an API, file share, or SFTP |
| `file` | File system watch | File appears in a monitored directory |

### Source Properties

| Property | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Unique identifier for this source |
| `trigger` | string | yes | Trigger type: `http`, `queue`, `schedule`, `pull`, `file` |
| `endpoint` | string | for http | HTTP path. Supports inline expressions: `/api/{$.category}` |
| `contentType` | string | no | Payload format. Default: `json`. Options: `json`, `csv`, `xml`, `text`, `binary`, `xlsx`, `parquet` |
| `map` | string | no | Transformation: `.elwood` file path or inline Elwood expression |
| `from` | object | for pull | Pull source configuration (see below) |

### Pull Sources

When `trigger: pull`, the `from` section specifies where to get the data:

```yaml
sources:
  - name: products
    trigger: pull
    contentType: json
    from:
      http:
        url: https://api.example.com/products
        method: GET
        headers:
          Authorization: Bearer ${API_TOKEN}
```

```yaml
sources:
  - name: inventory-file
    trigger: pull
    contentType: csv
    from:
      sftp:
        connectionString: ${SFTP_CONN}
        path: /exports/inventory.csv
```

```yaml
sources:
  - name: reports
    trigger: pull
    contentType: xlsx
    from:
      fileShare:
        connectionString: ${FS_CONN}
        path: /shared/reports/latest.xlsx
```

---

## Source Maps

The `map` property transforms raw source data into a normalized structure. It can be:

**An external `.elwood` script file** (recommended for complex transforms):
```yaml
map: normalize-orders.elwood
```

**An inline Elwood expression** (for simple transforms):
```yaml
map: $.data.results[*]
```

### How maps work

The map script receives:
- **`$`** — the raw payload (the data as received from the source)
- **`$source`** — source metadata (trigger info, headers, event ID, etc.)

The script returns the transformed data, which becomes the input for the rest of the pipeline.

**Example: `normalize-orders.elwood`**
```elwood
return {
  orders: $.data.orderList[*] | select o => {
    id: o.orderId,
    customer: o.customerName,
    total: o.amount,
    status: o.orderStatus,
    correlationId: $source.http.headers["X-Correlation-Id"]
  }
}
```

---

## Source Metadata (`$source`)

Every source provides metadata alongside the payload. Scripts access it via `$source`:

```
$source.name             → "orders" (source name from YAML)
$source.trigger          → "http" (trigger type)
$source.eventId          → "evt-abc-123" (unique execution ID)
$source.payloadId        → "pay-def-456" (unique payload ID)
$source.timestamp        → "2026-03-25T10:30:00Z"
```

### HTTP trigger metadata
```
$source.http.method      → "POST"
$source.http.path        → "/api/orders"
$source.http.headers     → { "Content-Type": "application/json", "X-Correlation-Id": "..." }
$source.http.query       → { "category": "shoes" }
```

### Queue trigger metadata
```
$source.queue.name       → "orders-q1"
$source.queue.messageId  → "msg-123"
$source.queue.metadata   → { "priority": "high" }
```

Scripts that don't need metadata can ignore `$source` — they work identically in the playground, CLI, and production.

---

## Outputs

An output defines what data to extract, how to transform it, and where to send it.

```yaml
outputs:
  - name: active-orders
    path: $.orders[*] | where o => o.status == "active"
    outputId: generate-id.elwood
    map: format-output.elwood
    contentType: json
    concurrency: 50
    destinations:
      fileShare:
        - connectionString: ${FS_CONN}
          filename: /exports/{$.id}.json
```

### Output Properties

| Property | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Unique name for this output |
| `path` | string | no | Filter/select expression or `.elwood` script. Selects which data goes to this output |
| `outputId` | string | no | Expression or `.elwood` script that generates a unique ID per output item |
| `map` | string | no | Transformation: `.elwood` file path or inline expression |
| `contentType` | string | no | Output format. Default: `json`. Options: `json`, `csv`, `xml`, `text`, `parquet`, `xlsx` |
| `concurrency` | int | no | Max parallel items for async executors. Default: `1` |
| `destinations` | object | no | Where to deliver the output (see below) |

### Output Processing Pipeline

Each output item flows through these stages in order:

```
Source data
  → path (filter/select — pick which items)
  → map (transform each item)
  → contentType (serialize to target format)
  → destinations (deliver)
```

### The `path` property

Selects which data from the source(s) goes to this output. Can be:

- **A JSONPath-style expression:** `$.orders[*]`
- **An Elwood pipe expression:** `$.orders[*] | where o => o.status == "active"`
- **An `.elwood` script file:** `filter-orders.elwood`

If omitted, the full source data is passed to the output.

### The `map` property

Transforms each item selected by `path`. Same syntax as source maps — `.elwood` file or inline expression.

### The `outputId` property

Generates a unique identifier for each output item. Used by executors for idempotency, tracking, and deduplication.

```yaml
outputId: $.id                                    # Simple field reference
outputId: generate-output-id.elwood               # Script that computes an ID
```

---

## Destinations

Destinations define where output data is delivered. An output can have multiple destinations (fan-out).

### File Share

```yaml
destinations:
  fileShare:
    - connectionString: ${FS_CONN}
      filename: /exports/orders/{$.id}.json
```

### SFTP

```yaml
destinations:
  sftp:
    - connectionString: ${SFTP_CONN}
      filename: /incoming/{$.code}.xml
```

### HTTP (POST/PUT)

```yaml
destinations:
  http:
    - url: https://api.target-system.com/import
      method: POST
      headers:
        Authorization: Bearer ${TARGET_TOKEN}
        Content-Type: application/json
```

### Blob Storage

```yaml
destinations:
  blobStorage:
    - connectionString: ${BLOB_CONN}
      container: output-data
      filename: orders/{$.id}.json
```

### Multiple Destinations

An output can go to multiple destinations simultaneously:

```yaml
destinations:
  fileShare:
    - connectionString: ${FS_CONN}
      filename: /exports/{$.id}.json
  http:
    - url: https://api.system-a.com/import
      method: POST
    - url: https://api.system-b.com/import
      method: POST
  blobStorage:
    - connectionString: ${BLOB_CONN}
      container: archive
      filename: archive/{$.id}.json
```

### Dynamic Filenames

Filenames and URLs support inline Elwood expressions:

```yaml
filename: /exports/{$.category}/{$.id}.json       # Data-driven path
filename: /daily/{$source.timestamp}.csv           # Timestamp from source metadata
```

---

## Join

When a pipeline has multiple sources, the `join` section defines how they're combined.

```yaml
sources:
  - name: orders
    trigger: http
    contentType: json

  - name: products
    trigger: pull
    contentType: json
    from:
      http:
        url: https://api.example.com/products

join:
  path: $
  keys:
    - orders.productId
    - products.id
```

Without a `join`, multiple sources are available as a merged object:
```
$.orders    → data from the "orders" source
$.products  → data from the "products" source
```

---

## Inline Expressions vs External Scripts

Values in the YAML can be:

| Type | Syntax | When to use |
|---|---|---|
| **Static** | `contentType: json` | Fixed configuration values |
| **Inline expression** | `path: $.orders[*]` | Simple data access, short filters |
| **External script** | `map: transform.elwood` | Complex logic, multiple pipes, let bindings |

**Guideline:** use inline for anything short and readable. Use external `.elwood` files for anything with multiple pipes, conditionals, or let bindings. This is a recommendation — not enforced.

**Examples of good inline:**
```yaml
path: $.orders[*]
path: $.items[*] | where i => i.active
outputId: $.id
filename: /exports/{$.code}.json
```

**Examples that should be external scripts:**
```yaml
map: complex-transform.elwood    # Has let bindings, conditionals, multiple pipes
outputId: generate-id.elwood     # Has logic beyond simple field access
```

---

## Environment Variables

Connection strings, tokens, and other secrets use `${VAR_NAME}` syntax:

```yaml
destinations:
  fileShare:
    - connectionString: ${FS_CONNECTION_STRING}
      filename: /exports/data.json
  http:
    - url: https://api.example.com/import
      headers:
        Authorization: Bearer ${API_TOKEN}
```

Environment variables are resolved at runtime by the executor. They are **not** Elwood expressions — they are simple string substitution for configuration values.

---

## Content Type Conversion

When a source has a non-JSON `contentType`, the executor automatically converts the raw data before passing it to scripts:

| contentType | What happens | `$` in scripts |
|---|---|---|
| `json` | Parsed as JSON | JSON object/array |
| `csv` | Parsed with `fromCsv()` | Array of objects (headers become keys) |
| `xml` | Parsed with `fromXml()` | JSON object (XML structure) |
| `text` | Passed as string | Raw string |
| `binary` | Base64-encoded | Base64 string |
| `xlsx` | Parsed with `fromXlsx()` | Array of objects (requires Elwood.Xlsx extension) |
| `parquet` | Parsed with `fromParquet()` | Array of objects (requires Elwood.Parquet extension) |

For outputs, the reverse happens — the `contentType` determines serialization format.

---

## Running Pipelines

### CLI Executor (development & testing)

```bash
# Single source — provide a data file
elwood pipeline run pipeline.elwood.yaml --source orders=payload.json

# Multi-source — one file per named source
elwood pipeline run pipeline.elwood.yaml \
  --source orders=orders-payload.json \
  --source products=products-data.json

# Outputs are written to stdout or --output-dir
elwood pipeline run pipeline.elwood.yaml \
  --source orders=payload.json \
  --output-dir ./output/
```

Source files can be:
- **Plain data files** — `$` is the file content, `$source` has minimal defaults
- **Envelope files** — JSON with `"source"` and `"payload"` keys, providing full `$source` metadata

**Envelope file example:**
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
    "orders": [
      { "id": "ORD-001", "customer": "Alice", "total": 150.00 }
    ]
  }
}
```

### Validate a pipeline

```bash
elwood pipeline validate pipeline.elwood.yaml
```

Checks:
- YAML syntax and schema
- All referenced `.elwood` scripts exist
- Elwood expressions parse without errors
- Source names are unique
- Required properties are present

---

## Complete Example: Multi-Source Pipeline

```yaml
version: 2
name: order-enrichment
description: |
  Receives orders via HTTP, enriches with product data from external API,
  outputs to file share and blob storage.

sources:
  - name: orders
    trigger: http
    endpoint: /api/orders
    contentType: json
    map: normalize-orders.elwood

  - name: products
    trigger: pull
    contentType: json
    from:
      http:
        url: https://api.products.example.com/catalog
        method: GET
        headers:
          Authorization: Bearer ${PRODUCT_API_TOKEN}

outputs:
  - name: enriched-orders
    path: enrich-with-products.elwood
    map: format-for-erp.elwood
    contentType: json
    concurrency: 100
    destinations:
      http:
        - url: https://erp.example.com/api/import
          method: POST
          headers:
            Authorization: Bearer ${ERP_TOKEN}
      blobStorage:
        - connectionString: ${BLOB_CONN}
          container: order-archive
          filename: orders/{$.orderId}/{$source.eventId}.json

  - name: order-summary
    path: $.orders[*] | select o => { id: o.id, total: o.total }
    contentType: csv
    destinations:
      fileShare:
        - connectionString: ${FS_CONN}
          filename: /reports/daily-orders.csv
```

**Supporting scripts:**

`normalize-orders.elwood`:
```elwood
return {
  orders: $.data.orderList[*] | select o => {
    id: o.orderId,
    customer: o.customerName,
    productCode: o.itemCode,
    total: o.amount,
    status: o.orderStatus,
    correlationId: $source.http.headers["X-Correlation-Id"]
  }
}
```

`enrich-with-products.elwood`:
```elwood
let orders = $.orders
let products = $.products

return orders[*] | select o => {
  ...o,
  productName: products[*] | first p => p.code == o.productCode | select p => p.name,
  productCategory: products[*] | first p => p.code == o.productCode | select p => p.category
}
```

`format-for-erp.elwood`:
```elwood
return $[*] | select o => {
  OrderID: o.id,
  CustomerName: o.customer.toUpper(),
  ProductName: o.productName,
  Category: o.productCategory,
  Amount: o.total.toString(),
  Status: o.status
}
```
