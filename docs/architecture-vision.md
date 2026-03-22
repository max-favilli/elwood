# Elwood Architecture Vision

## The Elwood Ecosystem

Elwood is not a single tool — it's a layered ecosystem where each layer is independently useful and builds on the one below it.

```
┌─────────────────────────────────────────────────┐
│  Executors (separate packages)                  │  Infrastructure-specific
│  Azure (ASB + Functions)                        │
│  AWS (SQS + Lambda) | K8s (Jobs)                │
├─────────────────────────────────────────────────┤
│  Elwood Runtime + CLI Executor                  │  Phase 3
│  Pipeline YAML → execution plan                 │
│  IExecutor, ISource, IDestination interfaces    │
│  Split/fan-out/merge orchestration              │
├─────────────────────────────────────────────────┤
│  Elwood Scripts + Format I/O                    │  Phase 2
│  Pure .elwood scripts as transformation maps    │
│  any format in → Elwood transform → any format  │
│  (JSON, CSV, XML, XLSX, Text)                   │
├─────────────────────────────────────────────────┤
│  Elwood Core + CLI                              │  Phase 1
│  DSL engine (parse, evaluate expressions)       │
│  .NET library, TypeScript library, CLI tool     │
└─────────────────────────────────────────────────┘
```

### Layer 1 — Elwood Core (Phase 1, complete)

The DSL engine. Parses and evaluates Elwood expressions against JSON input.

**Available as:**
- .NET library (`Elwood.Core`, `Elwood.Json`) — for embedding in .NET applications
- TypeScript library (`@elwood-lang/core`) — for browsers, Node.js, edge runtimes (Phase 1b)
- CLI tool (`elwood eval`, `elwood repl`) — for interactive use and shell scripting

**Use cases at this layer:**
- Embed JSON transformations in any application
- Replace hardcoded transformation logic with configurable expressions
- Interactive data exploration via REPL
- Shell pipelines: `cat data.json | elwood eval '$.users | where(.age > 30) | select(.name)'`

### Layer 2 — Elwood Scripts + Format I/O (Phase 2)

Pure Elwood scripts (`.elwood` files) serve as transformation maps. An earlier design proposed YAML documents where the tree structure mirrors the output shape, but this was abandoned due to deep-indentation problems and the impossibility of providing full editor tooling for two syntaxes (YAML + Elwood) in one file.

Scripts with `let` decomposition solve deep nesting, and `memo` handles repeated patterns. One syntax means full editor support (highlighting, autocomplete, error reporting).

Format conversion handles non-JSON inputs/outputs:
```
any format ──→ [input conversion] ──→ JSON ──→ Elwood script ──→ JSON ──→ [output conversion] ──→ any format
```

Supported formats: JSON (native), CSV, XML, XLSX, Text.

**Available as:**
- .NET library (format converters in `Elwood.Core`)
- CLI tool (`elwood run transform.elwood --input data.csv --input-format csv`)

**Use cases at this layer:**
- Define complex transformations as scripts with full editor tooling
- Transform between formats: CSV → JSON, XML → JSON, JSON → CSV, etc.
- Version-control transformation logic separately from application code
- `let` + `memo` keep large maps readable (e.g., 6500-line SAP IDoc map → 450-line script)

### Layer 3 — Elwood Runtime (Phase 3)

A pipeline execution engine that runs integration definitions written in pipeline YAML. YAML handles declarative orchestration (sources, destinations, triggers, connections); Elwood scripts handle transformation logic. It manages the full lifecycle: fetch data from sources, apply transformations, deliver results to destinations.

**Available as:**
- .NET library (`Elwood.Runtime`) — embed in any .NET application
- CLI tool (`elwood run pipeline.elwood.yaml`) — standalone execution
- Hosting packages for different execution models (see below)

**Use cases at this layer:**
- Data integration pipelines (ETL/ELT)
- API-to-API data transformation and routing
- File processing workflows
- Event-driven data pipelines

---

## Elwood Runtime — Deep Dive

### Core architecture

```
┌──────────────────────────────────────────────────────────┐
│                    Elwood Runtime                         │
│                                                          │
│  ┌─────────┐    ┌──────────────┐    ┌───────────────┐   │
│  │ Sources  │───→│  Transform   │───→│ Destinations  │   │
│  │ (fetch)  │    │  (Elwood     │    │ (deliver)     │   │
│  │          │    │   script)  │    │               │   │
│  └─────────┘    └──────────────┘    └───────────────┘   │
│       ↑                                     ↑           │
│  ┌─────────┐                        ┌───────────────┐   │
│  │ Source   │                        │ Destination   │   │
│  │ Adapters │                        │ Adapters      │   │
│  └─────────┘                        └───────────────┘   │
└──────────────────────────────────────────────────────────┘
```

### Pluggable adapters

Source and destination adapters are pluggable interfaces:

```csharp
// Conceptual — exact API TBD
interface IElwoodSource
{
    Task<JsonNode> FetchAsync(SourceConfig config, EvaluationContext context);
}

interface IElwoodDestination
{
    Task DeliverAsync(JsonNode data, DestinationConfig config, EvaluationContext context);
}
```

**Built-in source adapters (planned):**
- HTTP (REST API calls, webhooks)
- File system (local files, network shares)
- Blob storage (Azure Blob, S3)
- SFTP
- Database (SQL query → JSON)

**Built-in destination adapters (planned):**
- Blob storage
- SFTP
- HTTP (POST/PUT to API)
- File system
- Message queue (publish result)

Users can implement custom adapters for specialized systems.

### Hosting model — the key design challenge

The runtime library doesn't care about HOW or WHEN it's invoked. That's the host's job. Different hosts enable different execution patterns:

```
┌─────────────────────────────────────────┐
│         How you HOST the runtime        │
├──────────────┬──────────────────────────┤
│ CLI          │ elwood run pipeline.yaml │
│ ASP.NET      │ Embedded in HTTP handler │  ← synchronous
│ Worker       │ Background service       │  ← asynchronous
│ Azure Func   │ Serverless trigger       │  ← event-driven
│ Container    │ Docker/K8s job           │  ← batch
└──────────────┴──────────────────────────┘
```

| Hosting mode | Trigger | Lifecycle | Caller waits? |
|---|---|---|---|
| **CLI** | Command line | One-shot: run pipeline, exit | Yes |
| **ASP.NET middleware** | HTTP request | Request-scoped: fetch, transform, respond | Yes |
| **Background worker** | Queue message, schedule | Background: pick up work, process, deliver | No |
| **Serverless function** | Event (blob created, HTTP, timer) | Function-scoped: process single event | Depends |
| **Container job** | Orchestrator (K8s CronJob, etc.) | Batch: process all pending work, exit | No |

The runtime library is the same in all cases. The host decides:
- **When** to start a pipeline (trigger)
- **Whether** to wait for completion (sync vs async)
- **How** to handle errors (retry, dead-letter, alert)
- **Where** to report status (logs, metrics, notifications)

### Execution model — sync vs async

This is the fundamental tension:

**Synchronous (request-scoped):**
```
HTTP Request → Fetch Sources → Transform → Deliver → HTTP Response
              └──────────── within the request ─────────────────┘
```
- Caller waits for the result
- Simple error model: success or failure, returned to caller
- Timeout constraints (HTTP timeouts, function timeouts)
- Best for: API gateways, webhook handlers, real-time transformations

**Asynchronous (background):**
```
Trigger → Enqueue Work
                ↓
Worker picks up → Fetch Sources → Transform → Deliver → Mark Complete
```
- No caller waiting
- Complex error model: retries, partial failures, dead-letter queues
- No timeout constraints (can process 100MB+ documents)
- Best for: batch ETL, file processing, event-driven pipelines

**The runtime should support both** via a simple abstraction:

```csharp
// Conceptual
var runtime = new ElwoodRuntime(adapters);

// Sync — caller gets the result
var result = await runtime.ExecuteAsync(pipelineDefinition, input);

// Async — fire and forget, results go to destinations
await runtime.ExecuteAsync(pipelineDefinition, input);
// (destinations handle delivery)
```

The difference is really about **what happens with the output**: does the caller consume it, or does it go to configured destinations?

---

## Orchestration Model — Sync vs Async

### The fundamental problem

Synchronous pipelines are simple: trigger → fetch → transform → deliver → done. Everything happens in sequence within a single process.

Asynchronous pipelines are hard because of **fan-out**: one input becomes many parallel items, each processed concurrently, then results are aggregated back. This is the **Splitter → Parallel Processing → Aggregator** pattern.

```
TRIGGER → FETCH SOURCE → SPLIT (array) → FAN-OUT → PROCESS EACH → AGGREGATE → DELIVER
                              │                                        │
                         $.orders[*]                            merge results
                         N items                                back into one
                              │                                  document
                              ↓
                    ┌─── item 0 ──→ process ──→ result 0 ──┐
                    ├─── item 1 ──→ process ──→ result 1 ──┤
                    ├─── item 2 ──→ process ──→ result 2 ──┤──→ AGGREGATE
                    └─── item N ──→ process ──→ result N ──┘
```

Fan-out can repeat at multiple stages: sources fan out, then outputs fan out again.

### Industry approaches

Research across MuleSoft, Apache Camel, AWS Step Functions, Azure Durable Functions, Temporal, n8n, Prefect, and Dagster reveals two fundamental camps:

| Approach | How it works | Examples |
|---|---|---|
| **Pipeline as data** | YAML/JSON/XML describes the pipeline. An engine interprets and executes it. | Step Functions, MuleSoft, n8n |
| **Pipeline as code** | Code IS the pipeline. A framework adds durability via replay. | Temporal, Durable Functions, Prefect |

Elwood is in the **"pipeline as data"** camp. The YAML describes WHAT should happen. The execution infrastructure is someone else's concern.

### Key design insight from MuleSoft

MuleSoft keeps two languages strictly separate:
- **DataWeave** — transformation DSL (analogous to Elwood expressions)
- **XML flows** — orchestration topology (what connects to what, fan-out, routing)

DataWeave doesn't know about queues, parallelism, or retries. It just transforms data. The flow XML handles orchestration.

**Elwood should follow this pattern.** Elwood expressions handle transformation. The pipeline YAML handles orchestration topology. They are separate concerns.

### How Elwood YAML describes orchestration

The YAML defines the **pipeline topology** — steps, splits, merges, and delivery. It does NOT define the infrastructure (queues, functions, containers):

```yaml
version: 2

pipeline:
  - name: fetch-orders
    source:
      type: http
      method: GET
      url: `https://api.example.com/orders?date={$.triggerDate}`
      headers:
        Authorization: `Bearer {$.token}`
    transform: fetch-orders.elwood

  - name: enrich-each-order
    input: $.orders[*]                    # SPLIT: fan-out over this array
    concurrency: 50                       # hint to executor, not infrastructure
    source:
      type: http
      method: GET
      url: `https://api.example.com/details/{$.orderId}`
    transform:
      enrichedOrder: |
        { ...$.order, details: $.response.body, etag: $.response.headers.ETag }
    merge: $.orders[{index}]              # AGGREGATE: merge back by index

  - name: upload-results
    input: $.orders[*]                    # SPLIT again for outputs
    concurrency: 100
    destination:
      type: blob
      connectionString: ${BLOB_CONN}
      container: processed
      path: `/{$.orderId}.json`
    transform:
      content: $.enrichedOrder
```

### What the YAML defines vs what it doesn't

| Concern | In the YAML? | Notes |
|---|---|---|
| What sources to call | Yes | URLs, methods, headers |
| What transformation to apply | Yes | References to `.elwood` scripts |
| Where to split (fan-out) | Yes | `input: $.orders[*]` |
| How to merge results back | Yes | `merge: $.orders[{index}]` |
| Where to deliver output | Yes | Destination config |
| Concurrency hint | Yes | `concurrency: 50` |
| What queue/bus to use | No | Infrastructure concern |
| What compute to use | No | Infrastructure concern (Functions, Lambda, K8s) |
| How to retry on failure | Basic | Simple policy in YAML, advanced in executor config |
| What monitoring to use | No | Infrastructure concern |

The YAML is a **portable pipeline definition**. It describes the data flow and transformation, not the deployment topology.

### Executors — the execution layer

Different **executors** run the same pipeline YAML on different infrastructure:

```
Same pipeline.elwood.yaml
         │
    ┌────┴─────────────────────────────────────────┐
    │              │              │                 │
  CLI Executor   Azure Executor  AWS Executor    K8s Executor
  (sequential)   (ASB + Func)   (SQS + Lambda)  (Jobs)
  (ships with    (separate      (separate        (separate
   Elwood)        package)       package)         package)
```

**CLI Executor** (ships with Elwood):
- Runs everything sequentially, in-process
- Fan-out items processed one at a time in a loop
- No queues, no parallelism
- Perfect for development, testing, and small workloads

**Azure Executor** (separate package):
- Uses Azure Service Bus for fan-out messaging
- Azure Functions process each item concurrently
- Blob Storage for intermediate state
- This is what would replace Eagle's async processing

**AWS Executor** (separate package):
- Uses SQS for fan-out, Lambda for processing, S3 for state
- Or: Step Functions Map state for managed orchestration

**K8s Executor** (separate package):
- Kubernetes Jobs for fan-out processing
- ConfigMaps or PVCs for state

Elwood ships the Runtime library + CLI Executor. Infrastructure-specific executors are separate packages — built by the Elwood maintainer, or by the community.

### Updated architecture diagram

```
┌─────────────────────────────────────────────────┐
│  Executors (separate packages)                  │
│  Azure (ASB + Functions)                        │
│  AWS (SQS + Lambda)                             │
│  K8s (Jobs)                                     │
├─────────────────────────────────────────────────┤
│  Elwood Runtime + CLI Executor                  │  Phase 3
│  Parses pipeline YAML                           │
│  Builds execution plan (steps, splits, merges)  │
│  Drives execution via IExecutor interface       │
├─────────────────────────────────────────────────┤
│  Elwood Scripts + Format I/O                    │  Phase 2
│  Pure .elwood scripts as transformation maps    │
│  any format in → Elwood transform → any format  │
├─────────────────────────────────────────────────┤
│  Elwood Core + CLI                              │  Phase 1
│  DSL engine (parse, evaluate expressions)       │
│  .NET library, TypeScript library, CLI tool     │
└─────────────────────────────────────────────────┘
```

### Phase 3 implementation order

1. **Pipeline YAML spec** — define the schema for steps, sources, destinations, transforms
2. **Runtime library** — parse pipeline YAML, build step graph, define IExecutor/ISource/IDestination interfaces
3. **CLI Executor** — sequential execution, no fan-out (single source → transform → single destination)
4. **Fan-out/merge in CLI** — sequential fan-out (process items in a loop), proves the split/merge YAML semantics
5. **Azure Executor** — async fan-out via ASB + Functions (the Eagle replacement, likely a private package)

Each step is independently useful. Step 3 alone is enough for simple sync pipelines.

---

## Open Questions

These are design questions that need to be answered before or during Phase 3 implementation. They don't block earlier phases.

### 1. How much orchestration logic belongs in the YAML? — PARTIALLY RESOLVED

The YAML defines the **pipeline topology**: steps, sources, destinations, splits, merges, and transformations. It also includes **hints** like `concurrency: 50` that executors can use but aren't required to honor.

The YAML does NOT define infrastructure (queues, functions, containers) or advanced operational concerns (circuit breakers, dead-letter queues, monitoring). Those are executor configuration.

**What belongs in YAML:** step ordering, split points (`input: $.items[*]`), merge strategy, source/destination config, transformation references, basic retry policy.

**What belongs in executor config:** queue names, connection strings, compute targets, advanced error handling, monitoring integration.

**Remaining question:** Where exactly is the line for retry config? Basic retry (maxAttempts, backoff) feels portable enough for the YAML. Dead-letter queues and circuit breakers are infrastructure-specific.

### 2. Multi-source joins — how to handle partial state?

When a pipeline has multiple sources, they may arrive at different times in an async context:

- **Sync:** Fetch all sources in parallel, join when all are ready. Simple.
- **Async:** Source A arrives via HTTP trigger, Source B arrives 5 minutes later via a file drop. The runtime needs to store Source A's data until Source B arrives.

This requires **state management** — somewhere to keep partial pipeline state. Options:
- In-memory (only works for single-instance sync execution)
- External store (Redis, database, blob) — adds infrastructure dependency
- Let the host manage this (the runtime just joins what it's given)

**Likely answer:** Start with sync-only joins (all sources fetched at execution time). Async partial-state joins are a later feature, and the host should manage the state store.

### 3. Error handling and retry semantics

When a destination delivery fails:
- Retry the delivery? How many times? With what backoff?
- Retry the entire pipeline? (Sources may return different data on retry)
- Dead-letter the failed item?
- Continue processing remaining items or abort?

**In YAML or in host config?** If in YAML:
```yaml
outputs:
  - name: upload
    retry: { maxAttempts: 3, backoff: exponential }
    onFailure: dead-letter
```

If in host config, the YAML is simpler but less portable.

**Likely answer:** Basic retry config in YAML (it's part of the pipeline definition), advanced error handling (dead-letter queues, circuit breakers) in host config.

### 4. State management / deduplication

Some pipelines need to track what's already been processed to avoid duplicates. This requires persistent state (a record of processed item IDs with TTL).

Questions:
- Is dedup a runtime feature or a host feature?
- Where is the state stored? (The runtime shouldn't mandate a specific store)
- Is this even in scope for the open-source project, or is it a concern for specific deployments?

**Likely answer:** Define an `IStateStore` interface in the runtime. Ship a simple in-memory implementation for CLI/testing. Specific hosts provide real implementations (Redis, database, etc.).

### 5. Format conversion (non-JSON inputs) — RESOLVED

**Decision:** Format conversion is a Phase 2 concern, not a Phase 3 (Runtime) concern.

The CLI accepts `--input-format` and `--output-format` flags to convert non-JSON data to/from JSON before/after the Elwood script runs. Supported formats: JSON (native), CSV, XML, XLSX, Text.

This means the Runtime always receives and produces JSON — it doesn't need to know about format conversion.

See `docs/roadmap.md` Phase 2 for format conversion details.

### 6. What's the MVP for Phase 3? — RESOLVED

Phase 3 is implemented incrementally:

**Step 1 — Pipeline YAML spec + Runtime + CLI Executor (MVP):**
- [ ] Pipeline YAML schema (steps, sources, destinations, transforms)
- [ ] Runtime library: parse YAML, build step graph, define IExecutor/ISource/IDestination
- [ ] CLI Executor: sequential, in-process, no fan-out
- [ ] HTTP and file source adapters
- [ ] File and blob destination adapters
- [ ] `elwood run pipeline.yaml --input data.json`

**Step 2 — Fan-out/merge in CLI:**
- [ ] `input: $.items[*]` split semantics
- [ ] `merge: $.items[{index}]` aggregation
- [ ] Sequential fan-out (loop, no parallelism) — proves the YAML semantics

**Step 3 — Azure Executor (private package, Eagle replacement):**
- [ ] ASB for fan-out messaging
- [ ] Azure Functions for concurrent processing
- [ ] Blob Storage for intermediate state
- [ ] This is infrastructure-specific and likely stays private

Each step is independently useful.

---

## Cross-Platform Considerations

### Which layers work in TypeScript?

| Layer | .NET | TypeScript | Notes |
|---|---|---|---|
| Core (expressions) | Yes | Yes | Shared conformance suite |
| Scripts + Format I/O | Yes | Possible future | Format converters (CSV, XML) needed |
| Runtime (pipelines) | Yes | Limited | Source/dest adapters are I/O-heavy, more natural in .NET/Node.js than browser |

The Runtime layer is primarily a server-side concern. A TypeScript Runtime for Node.js is possible but not a priority — the .NET implementation serves the server-side use case, and the TS implementation serves the browser/edge use case where full pipeline execution isn't needed.

### The Playground (Phase 1c)

The TypeScript Core library enables a browser-based playground where users can:
- Write Elwood expressions with syntax highlighting and autocomplete
- Paste, upload, or drag-and-drop JSON input
- See transformed output in real-time
- Share playgrounds via URL or GitHub Gist

This only needs Layer 1 (Core) in the browser. Layers 2 and 3 are server-side.

See `docs/playground-spec.md` for the full specification.
