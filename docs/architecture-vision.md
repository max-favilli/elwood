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
│  Compiled Mode (optional)                       │  Phase 2b
│  AST → Expression Trees / JS code generation    │
│  Near-native performance for large workloads    │
├─────────────────────────────────────────────────┤
│  Elwood Scripts + Format I/O                    │  Phase 2
│  Pure .elwood scripts as transformation maps    │
│  fromCsv, toCsv, fromXml, toXml, fromText...   │
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

Format conversion is built into the language as functions, not external configuration:

```elwood
// Parse CSV with custom options, transform, output as CSV
let orders = $.rawCsv | fromCsv({ delimiter: ";", headers: true })
let result = orders | where(.amount > 100) | select({ id: .id, total: .amount })
return result | toCsv()
```

Format functions: `fromCsv`/`toCsv`, `fromXml`/`toXml`, `fromText`/`toText`. Each accepts an optional config object.

The CLI also provides `--input-format` / `--output-format` flags as shorthand for simple cases.

**Available as:**
- Built-in functions in `Elwood.Core` (available in both .NET and TypeScript)
- CLI flags: `elwood run transform.elwood --input data.csv --input-format csv`

**Use cases at this layer:**
- Define complex transformations as scripts with full editor tooling
- Transform between formats: CSV → JSON, XML → JSON, JSON → CSV, etc.
- Version-control transformation logic separately from application code
- `let` + `memo` keep large maps readable (e.g., 6500-line SAP IDoc map → 450-line script)

### Performance — Compiled Mode (Phase 2b)

The tree-walk interpreter with lazy evaluation is production-viable. Compiled mode eliminates interpretation overhead entirely:

- **.NET:** Compile AST → Expression Trees → JIT-compiled delegates. Near-native speed.
- **TypeScript:** Compile AST → generated JavaScript functions. V8 JIT optimizes them.

Compiled expressions are cached for reuse — critical when the same transform runs thousands of times (e.g., processing each item in a large array).

This is Phase 2b, not Phase 4, because performance matters before users adopt Elwood for production integration pipelines (Phase 3).

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

The YAML defines the **pipeline topology** — steps, splits, merges, and delivery. It does NOT define the infrastructure (queues, functions, containers).

**Hybrid approach:** YAML for structure, external `.elwood` scripts for any non-trivial expression. This avoids the two-syntax-in-one-file problem:

- **Inline OK:** static values (`trigger: http`), simple paths (`$.request.season`), short interpolation (`` `{$.code}-data` ``)
- **External `.elwood` required:** anything with pipes, conditionals, method chains, or multi-line logic

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
    transform: fetch-orders.elwood         # ← complex transform → external script

  - name: enrich-each-order
    input: $.orders[*]                     # SPLIT: fan-out over this array
    concurrency: 50                        # hint to executor
    source:
      type: http
      method: GET
      url: `https://api.example.com/details/{$.orderId}`
    transform: enrich-order.elwood         # ← external script
    merge: $.orders[{index}]               # AGGREGATE: merge back by index

  - name: upload-results
    input: $.orders[*]                     # SPLIT again for outputs
    concurrency: 100
    outputId: generate-output-id.elwood    # ← method chains → external script
    destination:
      type: blob
      connectionString: ${BLOB_CONN}
      container: processed
      path: build-path.elwood              # ← complex interpolation → external script
    transform: prepare-upload.elwood       # ← external script
```

External scripts are **reusable** (same script across multiple outputs) and **independently testable** via the CLI.

### What the YAML defines vs what it doesn't

| Concern | In the YAML? | How |
|---|---|---|
| What sources to call | Yes | URLs, methods, headers (inline or simple interpolation) |
| What transformation to apply | Yes | References to `.elwood` scripts |
| Where to split (fan-out) | Yes | `input: $.orders[*]` |
| How to merge results back | Yes | `merge: $.orders[{index}]` |
| Where to deliver output | Yes | Destination config (static values) |
| Complex dynamic values | Yes | References to `.elwood` scripts (outputId, paths, filenames) |
| Concurrency hint | Yes | `concurrency: 50` |
| What queue/bus to use | No | Infrastructure concern (executor config) |
| What compute to use | No | Infrastructure concern (executor config) |
| How to retry on failure | Basic | Simple policy in YAML, advanced in executor config |
| What monitoring to use | No | Infrastructure concern |

**Guideline (not enforced):** simple expressions stay inline, complex logic goes in external `.elwood` files. Short inline pipes like `$.items[*] | take(5)` are fine. This is a best practice — `elwood validate` may warn but won't reject.

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

### Deployment model — provision once, run all pipelines

The cloud infrastructure is provisioned **once** and runs **all** pipelines. Pipelines are deployed as configuration (YAML files), not as infrastructure. This is how every successful orchestration platform works (Airflow, Temporal, Logic Apps).

```
terraform apply (ONCE)
    │
    Creates the Elwood runtime platform:
    ├── Compute (Azure Functions / Lambda / K8s)
    │   ├── HTTP trigger      ← receives triggers for ANY pipeline
    │   ├── Queue trigger     ← processes fan-out messages for ANY pipeline
    │   └── Timer trigger     ← scheduled pipelines
    ├── Message bus (ASB / SQS)         ← shared for all pipelines
    ├── State store (Table Storage / DynamoDB)  ← state for all pipelines
    ├── Document store (Blob / S3)      ← large payloads for all pipelines
    ├── Pipeline store (Blob / S3)      ← where pipeline YAMLs live
    └── Monitoring (App Insights / CloudWatch)
```

Deploying a pipeline is just uploading YAML + `.elwood` scripts:

```bash
# One-time: provision the platform
cd elwood-infra/azure && terraform apply

# Deploy pipelines (as many as you want, any time)
elwood deploy product-sync.elwood.yaml
elwood deploy order-enrichment.elwood.yaml
elwood deploy daily-report.elwood.yaml
```

### How execution works (all pipelines, same functions)

```
HTTP request arrives for "product-sync"
    ↓
HTTP trigger function:
    1. Reads product-sync.elwood.yaml from pipeline store
    2. Creates execution state (IStateStore)
    3. Evaluates first step (fetch source, run Elwood transform)
    4. If fan-out → sends N messages to queue
    5. Returns execution ID to caller

Queue message arrives (fan-out item #47 of product-sync, step "enrich")
    ↓
Queue trigger function:
    1. Reads execution state
    2. Reads pipeline YAML
    3. Processes this one item (Elwood transform)
    4. Updates state (item #47 complete)
    5. If all items complete → triggers next step
    6. If next step has fan-out → sends N messages
```

Same functions handle every pipeline, every step, every fan-out. The pipeline YAML is the variable — the infrastructure is constant.

### Terraform modules (separate repo: `elwood-infra`)

```
elwood-infra/
├── modules/
│   ├── azure/                     # Elwood runtime on Azure
│   │   ├── main.tf                # Function App + ASB + Storage + Insights
│   │   ├── variables.tf           # resource names, SKUs, region
│   │   └── outputs.tf             # endpoints, connection strings
│   ├── aws/                       # Elwood runtime on AWS
│   │   ├── main.tf                # Lambda + SQS + DynamoDB + S3
│   │   └── ...
│   └── k8s/                       # Elwood runtime on Kubernetes
│       └── main.tf                # Deployments + Services + PVCs
└── examples/
    ├── azure-minimal/             # smallest viable setup
    └── azure-production/          # scaled-out with monitoring
```

### The complete user experience

```bash
# 1. One-time: provision your platform (any cloud)
cd elwood-infra/azure && terraform apply

# 2. Write pipelines
vim product-sync.elwood.yaml

# 3. Test locally (CLI executor, sequential)
elwood run product-sync.elwood.yaml --input test.json

# 4. Deploy to cloud (uploads YAML + scripts)
elwood deploy product-sync.elwood.yaml

# 5. Monitor
elwood status                       # all pipelines, recent executions
elwood status product-sync          # one pipeline's execution history
elwood status run-abc-123           # detailed state for one execution
```

### Updated architecture diagram

See the ecosystem diagram at the top of this document. The full stack from bottom to top: Core (Phase 1) → Format I/O (Phase 2) → Compiled Mode (Phase 2b) → Runtime (Phase 3) → Executors (separate packages) → Infrastructure (Terraform modules).

### Phase 3 implementation order

1. **Pipeline YAML spec** — define the schema for steps, sources, destinations, transforms
2. **Runtime library** — parse pipeline YAML, build step graph, define IExecutor/ISource/IDestination/IStateStore/IDocumentStore interfaces
3. **CLI Executor** — sequential execution, no fan-out (single source → transform → single destination)
4. **Fan-out/merge in CLI** — sequential fan-out (process items in a loop), proves the split/merge YAML semantics
5. **`elwood deploy` command** — uploads pipeline YAML + scripts to a configured pipeline store
6. **Azure Executor** — async fan-out via ASB + Functions (separate package, likely private)
7. **Terraform modules** — provision the Elwood runtime platform on Azure/AWS/K8s (separate repo: `elwood-infra`)

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

### 4. State management / deduplication — RESOLVED

#### The design principle

The state document is **metadata and pointers, not payloads**. Large data (trigger input, source responses, accumulated document) is stored separately via `IDocumentStore`. The state tracks IDs and progress, not content.

This is critical because:
- Trigger input can be 100MB+ (or GB for binary)
- The accumulated document grows as sources complete — could be very large
- Fan-out item results multiplied by thousands of items = massive if stored inline
- State needs to be readable/queryable for monitoring — embedding huge payloads makes that impractical

#### Pipeline Execution State Schema

```json
{
  "$schema": "https://elwood-lang.org/schemas/execution/v1.json",
  "executionId": "run-abc-123",
  "pipeline": "product-sync.elwood.yaml",
  "schemaVersion": 1,
  "status": "in_progress",
  "startedAt": "2026-03-24T10:00:00Z",
  "completedAt": null,

  "trigger": {
    "type": "http",
    "inputRef": "doc:run-abc-123:trigger-input"
  },

  "documentRef": "doc:run-abc-123:document",

  "steps": [
    {
      "name": "fetch-catalog",
      "status": "completed",
      "startedAt": "2026-03-24T10:00:01Z",
      "completedAt": "2026-03-24T10:00:02Z",
      "durationMs": 1250,
      "mergedAt": "$.catalog",
      "outputRef": "doc:run-abc-123:step:fetch-catalog:output"
    },
    {
      "name": "enrich-products",
      "status": "in_progress",
      "startedAt": "2026-03-24T10:00:03Z",
      "fanOut": {
        "splitPath": "$.catalog.products[*]",
        "total": 5000,
        "completed": 4823,
        "failed": 12,
        "pending": 165
      },
      "mergedAt": "$.catalog.products[{index}].enrichment"
    }
  ],

  "outputs": [
    {
      "name": "upload-to-cdn",
      "status": "in_progress",
      "total": 5000,
      "delivered": 3200,
      "failed": 0
    }
  ],

  "idm": {
    "key": "product-sync-2026-03-24",
    "ttl": "30d",
    "count": 4823
  },

  "errors": [
    {
      "step": "enrich-products",
      "itemIndex": 2,
      "error": "HTTP 429 Too Many Requests",
      "timestamp": "2026-03-24T10:00:15Z",
      "retryCount": 2
    }
  ]
}
```

Key design decisions:
- **`trigger.inputRef`** — pointer to the actual trigger data, not the data itself. Could be a blob URL, a file path, a document store key.
- **`documentRef`** — pointer to the accumulated JSON document. Stored separately because it can be very large.
- **`step.outputRef`** — pointer to each step's output data.
- **`fanOut`** — tracks progress counters only. Individual item results are stored separately via `IDocumentStore` (not inline in the state).
- **`idm`** — deduplication metadata. The actual set of processed IDs is managed by `IStateStore.CheckIdm`/`SaveIdm`, not stored inline.

#### Two interfaces — state vs documents

```csharp
// IStateStore — metadata and progress tracking (small, queryable)
interface IStateStore
{
    // Execution lifecycle
    Task<PipelineExecution> CreateExecution(string pipeline, TriggerInfo trigger);
    Task<PipelineExecution> GetExecution(string executionId);
    Task UpdateExecutionStatus(string executionId, ExecutionStatus status);

    // Step tracking
    Task UpdateStepStatus(string executionId, string stepName, StepStatus status);

    // Fan-out progress
    Task InitFanOut(string executionId, string stepName, int totalItems);
    Task RecordFanOutItemComplete(string executionId, string stepName, int index);
    Task RecordFanOutItemError(string executionId, string stepName, int index, string error);
    Task<FanOutProgress> GetFanOutProgress(string executionId, string stepName);

    // Output tracking
    Task UpdateOutputProgress(string executionId, string outputName, int delivered, int failed);

    // Deduplication (IDM)
    Task<bool> CheckIdm(string key, string itemId);
    Task SaveIdm(string key, string itemId, TimeSpan ttl);
}

// IDocumentStore — large data (blobs, files, whatever is efficient)
interface IDocumentStore
{
    Task<string> Save(string refKey, Stream data);     // returns ref
    Task<Stream> Load(string refKey);
    Task Delete(string refKey);
}
```

The split is intentional:
- **`IStateStore`** is small data, needs to be fast and queryable (Azure Tables, DynamoDB, Redis, PostgreSQL rows)
- **`IDocumentStore`** is large data, needs to handle big payloads efficiently (Azure Blob, S3, file system, PostgreSQL large objects)

#### Built-in implementations

| Implementation | IStateStore | IDocumentStore | Use case |
|---|---|---|---|
| `InMemory` | Dictionary | Dictionary | CLI executor, unit tests |
| `FileSystem` | JSON files | JSON files | Single-machine, development |
| `AzureStorage` | Azure Table Storage | Azure Blob Storage | Azure executor |
| `Aws` | DynamoDB | S3 | AWS executor |
| `PostgreSQL` | JSONB rows | Large objects or JSONB | Elwood DB integration |

The first two ship with Elwood. The rest are in executor packages.

#### Positioning

This is **Elwood's Pipeline Execution Schema** — a documented JSON schema and interface for tracking integration pipeline state. Executor authors implement `IStateStore` and `IDocumentStore` with their preferred storage backend. The schema is the same regardless of infrastructure.

Elwood offers this based on real-world experience building integration middleware. If the schema proves useful and Elwood gains adoption, it becomes a de facto standard — not through specification committees, but through practical adoption.

#### What this enables

| Capability | How |
|---|---|
| `elwood status run-abc-123` | CLI reads state from any IStateStore — same output regardless of executor |
| Monitoring dashboard | Reads PipelineExecution JSON — works with any executor |
| Migrate middleware to new cloud | Reimplement IStateStore + IDocumentStore, keep everything else |
| Debug failed pipeline | Inspect standard JSON state, examine step-by-step progress |
| Build a new executor | Schema tells you exactly what to store, interfaces tell you how to expose it |
| Test executors | Standard test fixtures with known state shapes |

### 5. Format conversion (non-JSON inputs) — RESOLVED

**Decision:** Format conversion is a Phase 2 concern, not a Phase 3 (Runtime) concern.

Two approaches, both in Phase 2:
- **In-script functions:** `fromCsv()`, `toCsv()`, `fromXml()`, `toXml()`, `fromText()`, `toText()` — full control with custom options, can mix formats in one script
- **CLI flags:** `--input-format csv`, `--output-format csv` — shorthand for simple cases

The Runtime always receives and produces JSON — it delegates format conversion to the Elwood script or CLI layer.

See `docs/roadmap.md` Phase 2 for details and examples.

### 6. What's the MVP for Phase 3? — RESOLVED

Phase 3 is implemented incrementally:

**Step 1 — Pipeline YAML spec + State schema + Runtime + CLI Executor (MVP):**
- [ ] Pipeline YAML schema (steps, sources, destinations, transforms)
- [ ] Pipeline Execution State JSON schema (v1)
- [ ] `IStateStore` + `IDocumentStore` interfaces
- [ ] `InMemoryStateStore` + `InMemoryDocumentStore` implementations
- [ ] Runtime library: parse YAML, build step graph, drive execution
- [ ] CLI Executor: sequential, in-process, no fan-out
- [ ] HTTP and file source adapters
- [ ] File and blob destination adapters
- [ ] `elwood run pipeline.yaml --input data.json`
- [ ] `elwood status <execution-id>` — reads state from IStateStore

**Step 2 — Fan-out/merge in CLI:**
- [ ] `input: $.items[*]` split semantics
- [ ] `merge: $.items[{index}]` aggregation
- [ ] Sequential fan-out (loop, no parallelism) — proves the YAML semantics
- [ ] Fan-out progress tracking via IStateStore
- [ ] `FileSystemStateStore` + `FileSystemDocumentStore` for persistent local state

**Step 3 — IDM (deduplication):**
- [ ] `CheckIdm` / `SaveIdm` with TTL
- [ ] In-memory and file-system implementations

**Step 4 — Azure Executor (private package, Eagle replacement):**
- [ ] `AzureTableStateStore` + `AzureBlobDocumentStore`
- [ ] ASB for fan-out messaging
- [ ] Azure Functions for concurrent processing
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

---

## Phase 5 Vision — Elwood DB

A JSON database where you store documents and query them with Elwood. No predefined schema, no document size limits, PostgreSQL as the storage backend.

### The idea

No existing database combines these three properties:
1. **Pipe-based functional query language** — the same Elwood you already know for transformation
2. **No document size limits** — automatic splitting handles 100MB+ documents
3. **Schema-on-write** — you define structure when storing, not upfront

```
-- MongoDB MQL
db.users.find({ age: { $gt: 25 }, active: true }, { name: 1 }).sort({ name: 1 }).limit(10)

-- PostgreSQL
SELECT jsonb_build_object('name', doc->'name') FROM docs
WHERE (doc->>'age')::int > 25 AND (doc->>'active')::boolean = true
ORDER BY doc->>'name' LIMIT 10;

-- Elwood DB
users | where(.age > 25 and .active) | select(.name) | orderBy(.name) | take(10)
```

Same language for querying and transforming. No context switch.

### Storage architecture — PostgreSQL backend, fixed schema

Two tables. No dynamic table creation, ever.

```sql
-- Collections (like MongoDB collections)
CREATE TABLE collections (
    name        TEXT PRIMARY KEY,
    split_path  TEXT,                    -- e.g., "$.orders[*]"
    config      JSONB DEFAULT '{}'
);

-- Chunks (ALL data lives here)
CREATE TABLE chunks (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    collection    TEXT NOT NULL REFERENCES collections(name),
    document_id   UUID NOT NULL,         -- groups chunks from same source document
    array_index   INT,                   -- position in split array (null if unsplit)
    data          JSONB NOT NULL,
    created_at    TIMESTAMPTZ DEFAULT NOW()
);
```

Every collection, every document, every chunk — same two tables. PostgreSQL handles storage, indexing, ACID, concurrency, backup, replication.

### How document splitting works

**Without splitting** — small document, stored as one chunk:
```
Store: { "name": "Alice", "age": 30 } into "users"
  → 1 row in chunks table
```

**With splitting** — large document, split into chunks:
```
Store: { "orders": [{ "id": 1, ... }, ... 200K items] } into "orders"
Collection split_path: "$.orders[*]"
  → 200K rows in chunks table, one per array item
```

One 100MB document becomes 200K small JSONB rows. PostgreSQL handles millions of rows effortlessly.

### How indexing works

PostgreSQL has built-in JSONB indexing. Two kinds:

**GIN index** — indexes all paths automatically (created once):
```sql
CREATE INDEX idx_chunks_data ON chunks USING GIN (data);
```

**Expression indexes** — fast lookup on specific paths (created per user-defined index):
```sql
-- "index .status on orders"
CREATE INDEX idx_orders_status ON chunks ((data->>'status'))
    WHERE collection = 'orders';

-- "index .total on orders" (numeric)
CREATE INDEX idx_orders_total ON chunks (((data->>'total')::numeric))
    WHERE collection = 'orders';
```

One `CREATE INDEX` statement per user-defined index. PostgreSQL's query planner uses them automatically.

### Elwood syntax for collections

```bash
# Create a collection with split and index hints
elwood db create orders --split '$.orders[*]' --index .status --index .total --index .customerId

# Store a document (automatically splits per config)
elwood db store orders --input huge-orders.json

# Query
elwood db query orders "where(.status == 'shipped') | select(.id, .customerName) | take(10)"
```

REPL:
```
elwood> :db connect postgres://localhost/elwooddb
elwood> :db use orders
elwood> $ | where(.status == "shipped") | select({ id: .id, customer: .customerName }) | take(10)
```

### Query translation — Elwood to SQL

The DB layer translates Elwood queries into optimized PostgreSQL queries:

```
Elwood:
  orders | where(.status == "shipped" and .total > 100) | select({ id: .id, name: .customerName.toUpper() }) | take(10)

Push to PostgreSQL:                          Execute in Elwood:
  WHERE data->>'status' = 'shipped'            select({ id, name: .customerName.toUpper() })
  AND (data->>'total')::numeric > 100          (toUpper is Elwood-side, not SQL)
  LIMIT 10
```

Start simple: push `where` (simple comparisons), `take`/`skip`, `orderBy`, and `count` to SQL. Everything else executes in Elwood on the result set. Optimize by pushing more operators down over time.

### What PostgreSQL handles for us

| Concern | PostgreSQL | Elwood DB layer |
|---|---|---|
| Storage on disk | Yes | — |
| ACID transactions | Yes | — |
| Concurrent access | Yes | — |
| JSONB indexing (GIN) | Yes | — |
| Expression indexes | Yes | Issues CREATE INDEX |
| Query planning | Yes | Translates Elwood → SQL |
| Backup / replication | Yes | — |
| Document splitting | — | Parses JSON, inserts rows |
| Elwood query parsing | — | Uses Elwood.Core |
| Non-SQL transformations | — | Runs Elwood on result set |

We're not building a database engine. We're building a **query translation layer** on top of PostgreSQL.

### How it fits in the ecosystem

```
┌─────────────────────────────────────────────────┐
│  Elwood DB                                      │  Phase 5
│  Store JSON, query with Elwood                  │
│  PostgreSQL backend, automatic splitting        │
├─────────────────────────────────────────────────┤
│  Executors (Azure, AWS, K8s)                    │  Separate packages
├─────────────────────────────────────────────────┤
│  Elwood Runtime + CLI Executor                  │  Phase 3
├─────────────────────────────────────────────────┤
│  Compiled Mode                                  │  Phase 2b
├─────────────────────────────────────────────────┤
│  Elwood Scripts + Format I/O                    │  Phase 2
├─────────────────────────────────────────────────┤
│  Elwood Core + CLI                              │  Phase 1
└─────────────────────────────────────────────────┘
```

### Open questions for Phase 5

1. **Embedded or server?** SQLite backend for embedded single-process use (like SQLite), or PostgreSQL for multi-client server? Could support both — SQLite for dev/testing, PostgreSQL for production.

2. **Update semantics** — when you update one field in a split document, do you update one chunk (fast) or reconstruct the whole document (consistent)? Chunk-level updates are faster but can lead to inconsistency if the update depends on sibling chunks.

3. **Cross-document queries** — can you join across collections? (`orders | join(customers, .customerId == .id)`) This maps well to Elwood's existing `join` operator but requires the DB to read from two collections.

4. **Aggregation push-down** — `count`, `sum`, `min`, `max` can be pushed to PostgreSQL for performance. `groupBy` is harder. How far do we go with query translation?

5. **Change streams** — can clients subscribe to changes? ("notify me when a new order with status = shipped is stored") This would make Elwood DB useful for event-driven architectures.

6. **Separate project or monorepo?** Elwood DB has a PostgreSQL dependency that Core/Runtime don't. Probably a separate repo: `github.com/max-favilli/elwood-db`.
