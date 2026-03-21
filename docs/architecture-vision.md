# Elwood Architecture Vision

## The Elwood Ecosystem

Elwood is not a single tool — it's a layered ecosystem where each layer is independently useful and builds on the one below it.

```
┌─────────────────────────────────────────────────┐
│  Elwood Runtime                                 │  Phase 3
│  Executes integration pipelines defined in YAML │
│  (sources → transform → destinations)           │
├─────────────────────────────────────────────────┤
│  Elwood YAML                                    │  Phase 2
│  Declarative transformation documents           │
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

### Layer 2 — Elwood YAML (Phase 2)

A declarative document format for describing data transformations. The YAML structure mirrors the desired output shape; leaf values are Elwood expressions. Supports multiple input and output formats — format conversion is built in.

```
any format ──→ [input conversion] ──→ JSON ──→ Elwood transform ──→ JSON ──→ [output conversion] ──→ any format
```

Supported formats: JSON (native), CSV, XML, XLSX, Text.

**Available as:**
- .NET library (`Elwood.Yaml`)
- CLI tool (`elwood run transform.elwood.yaml --input data.csv`)

**Use cases at this layer:**
- Define complex, multi-step transformations as documents rather than code
- Transform between formats: CSV → JSON, XML → JSON, JSON → CSV, etc.
- Version-control transformation logic separately from application code
- Share transformation definitions between teams/services
- Non-developers can read and understand the transformation shape

### Layer 3 — Elwood Runtime (Phase 3)

A pipeline execution engine that runs integration definitions written in Elwood YAML. It handles the full lifecycle: fetch data from sources, apply transformations, deliver results to destinations.

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
│  │          │    │   YAML map)  │    │               │   │
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

## Open Questions

These are design questions that need to be answered before or during Phase 3 implementation. They don't block earlier phases.

### 1. How much orchestration logic belongs in the YAML?

The YAML defines WHAT to do. But how much of the HOW should it also define?

**Minimal approach:** YAML only describes sources, transforms, destinations. Error handling, retries, notifications are host-level configuration.

**Maximal approach:** YAML includes retry policies, notification rules, timeout config, error handlers — everything needed to run the pipeline is in one file.

**Trade-off:** Maximal is self-contained (one file = one pipeline, fully described) but couples the definition to a specific execution environment. Minimal keeps the YAML portable but requires host-level configuration everywhere it's deployed.

**Likely answer:** Start minimal. Add orchestration features to the YAML only when there's a clear pattern that repeats across multiple hosts.

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

**Decision:** Format conversion is a Phase 2 (YAML layer) concern, not a Phase 3 (Runtime) concern.

Elwood YAML documents declare their input and output formats. The YAML layer handles conversion to/from JSON transparently. Supported formats: JSON (native), CSV, XML, XLSX, Text.

This means the Runtime always receives and produces JSON — it doesn't need to know about format conversion. It delegates that entirely to the YAML transformation layer.

See `docs/roadmap.md` Phase 2 for format conversion details.

### 6. What's the MVP for Phase 3?

A minimal viable runtime that proves the architecture without solving every problem:

- [ ] Single source → transform → single destination
- [ ] CLI hosting only (`elwood run pipeline.yaml`)
- [ ] HTTP and file source adapters
- [ ] File and blob destination adapters
- [ ] No retries, no joins, no state management
- [ ] No async execution (sync/CLI only)

This is enough to validate the YAML format and adapter architecture. Everything else is iterative enhancement.

---

## Cross-Platform Considerations

### Which layers work in TypeScript?

| Layer | .NET | TypeScript | Notes |
|---|---|---|---|
| Core (expressions) | Yes | Yes | Shared conformance suite |
| YAML (transforms) | Yes | Possible future | Needs YAML parser (js-yaml) |
| Runtime (pipelines) | Yes | Limited | Source/dest adapters are I/O-heavy, more natural in .NET/Node.js than browser |

The Runtime layer is primarily a server-side concern. A TypeScript Runtime for Node.js is possible but not a priority — the .NET implementation serves the server-side use case, and the TS implementation serves the browser/edge use case where full pipeline execution isn't needed.

### The Playground (Phase 4)

The TypeScript Core library enables a browser-based playground where users can:
- Write Elwood expressions
- Paste or upload JSON input
- See transformed output in real-time
- Share expressions via URL

This only needs Layer 1 (Core) in the browser. Layers 2 and 3 are server-side.
