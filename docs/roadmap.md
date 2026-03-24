# Elwood Roadmap

## Vision

Elwood is a standalone JSON transformation DSL inspired by KQL pipes, LINQ lambdas, and JSONPath navigation. It is designed for high performance, rich error reporting, and cross-platform use.

The .NET implementation is the reference engine. A TypeScript implementation provides the same language in browsers, Node.js, and edge runtimes. Both implementations share a conformance test suite to guarantee identical behavior.

---

## Phase 1 — Standalone DSL Engine ✅ (Complete)

**Goal:** A working JSON transformation engine.

- [x] Solution structure: Elwood.Core, Elwood.Json, Elwood.Newtonsoft (placeholder), Elwood.Cli
- [x] Lexer and recursive descent parser
- [x] AST with 30+ node types
- [x] Tree-walking evaluator
- [x] IElwoodValue abstraction (decoupled from JSON library)
- [x] System.Text.Json adapter (JsonNode)
- [x] JSONPath navigation: `$`, `$.field`, `$[*]`, `$[0]`, `$[2:5]`, `$[-2:]`, `$..field`
- [x] Auto-mapping: property access maps over arrays
- [x] KQL-style pipe operators: `where`, `select`, `selectMany`, `orderBy`, `groupBy`, `distinct`, `take`, `skip`, `batch`, `join` (inner/left/right/full), `concat`, `index`, `reduce`, `count`, `sum`, `min`, `max`, `first`, `last`, `any`, `all`, `match`
- [x] Named lambda expressions: `x => expr`, `(acc, x) => expr`
- [x] Implicit `$` context in pipe operations
- [x] `let` bindings (script-scoped variables)
- [x] `memo` functions (memoized lambdas for expensive computations)
- [x] `if`/`then`/`else` conditionals
- [x] Pattern matching: `| match "val" => result, _ => default`
- [x] Spread operator: `{ ...obj, newProp: val }`
- [x] String interpolation: `` `Hello {$.name}` ``
- [x] Arithmetic, boolean, and comparison operators (including string comparison)
- [x] 70+ built-in methods (string, numeric, datetime, crypto, null-checks, etc.)
- [x] Rich error reporting with line/column, "Did you mean?" suggestions
- [x] CLI tool: REPL, eval, run modes, stdin pipe support
- [x] 90 tests (65+ file-based with explanations, 25 code-based)
- [x] Documentation: syntax reference, changelog, 65 explanation files as tutorials

---

## Phase 1b — Cross-Platform (TypeScript Port)

**Goal:** Make Elwood available in browsers, Node.js, Deno, Bun, and edge runtimes (Cloudflare Workers, Vercel Edge) via a TypeScript implementation that is behaviorally identical to the .NET reference engine.

**Detailed implementation plan:** See [`docs/typescript-port-plan.md`](typescript-port-plan.md)

### Lazy evaluation (prerequisite — .NET engine) ✅

- [x] `LazyArrayValue` wraps `IEnumerable<IElwoodValue>` without materializing; streaming operators (`where`, `select`, `selectMany`, `take`, `skip`, `distinct`, `index`) return lazy arrays
- [x] `$[*]` wildcard returns lazy array (critical for short-circuit)
- [x] Only materializing operators call `.ToList()`: `orderBy`, `groupBy`, `count`, `sum`, `min`, `max`, `last`, `batch`, `join`, `reduce`
- [x] `take(n)` short-circuits — `take(1)` on 100K items: 19ms vs 400ms+ before
- [x] `first` short-circuits without materializing
- [x] Pipeline endpoints materialize via `ToConcreteValue()` for final JSON output
- [x] 4 benchmark tests with results logged to `tests/Elwood.Core.Tests/Benchmarks/results.log` (last 10 runs)
- [x] All 95 tests pass (91 conformance + 4 benchmarks)

### Pre-restructure cleanup ✅

- [x] All 48 explanation files cleaned — legacy system references replaced with generic terms
- [x] Sample data and examples updated
- [x] Source code comments cleaned

### Repo restructure ✅
- [x] 68 test cases extracted to `spec/test-cases/{name}/` (per-directory: script.elwood, input.json, expected.json, explanation.md)
- [x] .NET code moved under `dotnet/` (`dotnet/src/`, `dotnet/tests/`, `dotnet/Elwood.slnx`)
- [x] FileBasedTests.cs updated to discover from `spec/test-cases/`
- [x] CLAUDE.md updated with new structure
- [x] 97/97 tests pass after restructure

### TypeScript implementation (in progress)
- [x] Initialize TS project (`ts/`) with TypeScript, Vitest, zero runtime dependencies
- [x] Conformance test runner that reads from `../../spec/test-cases/` — 66 tests discovered, all fail with "Not implemented"
- [x] Port lexer (28 unit tests passing)
- [x] Port parser (24 unit tests passing)
- [x] Port evaluator (tree-walk, scope management, auto-mapping, JSONPath navigation)
- [x] Port built-in functions — 70+ methods:
  - [x] Array/pipe operators (where, select, orderBy, groupBy, join, reduce, etc.)
  - [x] String methods (toLower, replace, split, urlEncode, sanitize, etc.)
  - [x] Numeric methods (round, floor, ceiling, convertTo, etc.)
  - [x] DateTime methods (now, dateFormat, dateAdd, toUnixTimeSeconds, etc.)
  - [x] Crypto methods (hash via node:crypto MD5, rsaSign via node:crypto RSA-SHA1+PKCS1)
  - [x] Null-check, object, and type methods
- [x] Error reporting with line/column and "Did you mean?" suggestions
- [x] All 66 conformance tests passing (122 total: 28 lexer + 26 parser + 67 conformance + 1 discovery)

### Keeping in sync (ongoing)
New features follow the workflow: spec test case first → implement in .NET → implement in TS → update docs. CI runs both conformance suites on every PR.

---

## Phase 1c — Publish & Package

**Goal:** Open-source release of Elwood with both implementations, CI, and package distribution.

**Depends on:** Phase 1b complete (repo is in final structure, both implementations pass conformance suite).

### Open source ✅
- [x] Published to GitHub: https://github.com/max-favilli/elwood
- [x] README.md with quick start, examples, CLI download links, playground screenshot
- [x] LICENSE file (MIT)

### CI/CD ✅
- [x] GitHub Actions CI: build + test on push (both .NET and TS) — `.github/workflows/ci.yml`
- [x] Conformance gate: both implementations must pass all `spec/test-cases/`
- [x] Automated release workflow (`.github/workflows/release.yml`) — triggered on version tags
- [x] v0.1.0 released with native binaries for Windows, macOS, Linux

### .NET distribution ✅
- [x] NuGet package metadata (Elwood.Core, Elwood.Json — multi-target net8.0;net10.0)
- [x] `global.json` pinning SDK version
- [x] `dotnet tool` configured (`dotnet tool install --global Elwood.Cli` → `elwood` command)
- [x] Native AOT binaries on GitHub Release (linux-x64, macos-x64, win-x64)
- [ ] Publish NuGet packages to nuget.org (needs `NUGET_API_KEY` secret — account unlock pending)

### TypeScript distribution ✅
- [x] npm package published: `@elwood-lang/core` on npmjs.com
- [x] Browser compatibility documented (crypto limitation in known-issues.md)

### Playground ✅
- [x] Browser-based interactive playground: https://max-favilli.github.io/elwood/
- [x] Monaco Editor with Elwood syntax highlighting, autocomplete, snippet placeholders
- [x] Example gallery (68 examples from `spec/test-cases/`) with search, categories, rendered markdown explanations
- [x] File loading (local file picker + drag-and-drop, stays in browser)
- [x] Shareable links via lz-string URL compression
- [x] GitHub Pages deployment via GitHub Actions (`.github/workflows/deploy-playground.yml`)
- [x] **Spec:** [`docs/playground-spec.md`](playground-spec.md)

### API container
- [x] `Elwood.Api` project — minimal ASP.NET API: `POST /api/evaluate` + `/health`
- [x] Dockerfile (multi-stage, Alpine-based)
- [x] GitHub Actions workflow to build and push container on release tags (`.github/workflows/docker.yml`)
- [x] Usage: `docker run -p 8080:8080 ghcr.io/max-favilli/elwood-api`
- [x] Enables integration with any iPaaS via HTTP

### Remaining engine work
- [x] `iterate(seed, fn)` and `takeWhile` — lazy sequence generation
- [x] `.parseJson()` — deserialize embedded JSON strings
- [ ] Identify and port any remaining common JSON transformation functions not yet covered

---

## Phase 2 — Elwood Scripts as Maps + Multi-Format I/O

**Goal:** Use pure Elwood scripts (`.elwood` files) as the transformation format, with format conversion for non-JSON inputs/outputs. No YAML maps.

### Design decision: Scripts, not YAML maps

An earlier design proposed YAML documents where the tree structure mirrors the output shape and leaf values are Elwood expressions. This was abandoned for two reasons:

1. **Deep indentation** — A 20-level deep output (common in SAP IDocs, complex XML) means 40+ spaces of indent before the content. YAML's indentation-as-structure makes depth visible and painful.
2. **Two syntaxes in one file** — YAML for structure + Elwood for values means no editor can fully syntax-highlight, autocomplete, or error-check both. Every YAML-embedded-DSL (GitHub Actions + bash, Helm + Go templates) suffers from this — the embedded language gets zero tooling.

**Pure Elwood scripts solve both problems:**
- One syntax → full editor support (highlighting, autocomplete, error reporting)
- `let` decomposition flattens deep nesting into named, testable pieces
- `memo` handles repeated patterns (e.g., 82 SAP segments → 30 one-liner function calls)
- Already works today with `elwood run script.elwood --input data.json`

### Script-based maps

```elwood
// artmas09.elwood — replaces a 6500-line JSON map

let ausprt = memo (src, charName) => {
  "@SEGMENT": "1", FUNCTION: "009", CHAR_NAME: charName,
  MATERIAL_LONG: $.SAP_STUFF.MATERIAL_LONG,
  CHAR_VALUE_LONG: src.CHAR_VALUE_LONG
}

let ediDc40 = { "@SEGMENT": "1", TABNAM: "EDI_DC_40", MANDT: "400", ... }
let matHead = { "@SEGMENT": "1", MATL_TYPE: $.SAP_STUFF.MATL_TYPE, ... }

return {
  ARTMAS09: {
    IDOC: {
      "@BEGIN": "1",
      EDI_DC40: ediDc40,
      E1BPE1MATHEAD: matHead,
      E1BPE1AUSPRT: [
        ausprt($.C8_PRODUCTSUBTYPE, "C8_PRODUCTSUBTYPE"),
        ausprt($.JW_PRODUCTGROUP, "JW_PRODUCTGROUP"),
        // ... 30 more one-liners instead of 82 copy-pasted map nodes
      ]
    }
  }
}
```

### Format conversion

Elwood operates on JSON internally. Format converters handle non-JSON inputs/outputs:

```
any format ──→ [input conversion] ──→ JSON ──→ Elwood script ──→ JSON ──→ [output conversion] ──→ any format
```

| Format | Input (→ JSON) | Output (JSON →) |
|---|---|---|
| **JSON** | Native (no conversion) | Native (no conversion) |
| **CSV** | Rows → array of objects | Array of objects → rows |
| **XML** | Elements → objects, attributes → `@` properties | Objects → elements |
| **XLSX** | Sheet → array of objects | Array of objects → sheet |
| **Text** | Content as string or line-split array | Join array / render string |

Two ways to use format conversion:

**CLI flags** — for simple cases where the entire input is one format and the entire output is another:
```bash
elwood run transform.elwood --input data.csv --input-format csv
elwood run transform.elwood --input data.xml --output result.csv --output-format csv
```

**In-script functions** — for full control within the script (custom options, mixed formats, converting mid-pipeline):
```elwood
// Parse CSV with custom delimiter
let orders = $.rawCsv | fromCsv({ delimiter: ";", headers: true })

// Transform
let result = orders | where(.amount > 100) | select({ id: .id, total: .amount })

// Output as CSV
return result | toCsv()
```

```elwood
// Parse XML, transform, output as JSON
let items = $.xmlPayload | fromXml()
return items.catalog.products | select({ sku: .@id, name: .title })
```

```elwood
// Mix formats in one script
let products = $.csvData | fromCsv({ headers: true })
let categories = $.xmlCategories | fromXml()
return products | select(p => {
  ...p,
  categoryName: categories.list[*] | first(c => c.@id == p.catId) | select(.name)
})
```

### Format functions

| Function | Description | Options | Status |
|---|---|---|---|
| `fromCsv(options?)` | Parse CSV string → array of objects | `delimiter`, `headers`, `quote`, `skipRows`, `parseJson` | ✅ |
| `toCsv(options?)` | Array of objects → CSV string | `delimiter`, `headers`, `alwaysQuote` | ✅ |
| `fromXml(options?)` | Parse XML string → JSON object | `attributePrefix`, `stripNamespaces` | ✅ |
| `toXml(options?)` | JSON object → XML string | `rootElement`, `attributePrefix`, `declaration` | ✅ |
| `fromXlsx(options?)` | Parse XLSX (base64) → array of objects | `headers`, `sheet` *Extension* | ✅ |
| `toXlsx(options?)` | Array of objects → XLSX (base64) | `headers`, `sheet` *Extension* | ✅ |
| `fromParquet(options?)` | Parse Parquet (base64) → array of objects | *Extension* | ✅ |
| `toParquet(options?)` | Array of objects → Parquet (base64) | `schema`, `compression` *Extension, .NET only* | ✅ |
| `fromText(options?)` | Split text into lines or structured data | `delimiter` (default `\n`) | ✅ |
| `toText(options?)` | Join array into text | `delimiter` (default `\n`) | ✅ |

### Tasks
- [x] Built-in format functions: `fromCsv`, `toCsv`, `fromText`, `toText`
- [x] Built-in format functions: `fromXml`, `toXml`
- [x] CLI `--input-format` and `--output-format` flags (auto-detect from file extension, or explicit override)
- [x] CLI integration tests (13 tests: eval, run, format detection, output conversion, stdin, error handling)
- [x] Format converters — CSV:
  - [x] Configurable delimiter, headers, quote character
  - [x] `skipRows` — skip metadata/title rows before data
  - [x] `headers: false` — auto-generated alphabetic column names (A, B, C, ..., AA, AB)
  - [x] `parseJson: true` — auto-detect and deserialize JSON values in cells
  - [x] `alwaysQuote` — force-quote all fields in toCsv output
- [x] Format converters — Text: line splitting/joining with configurable delimiter
- [x] Format converters — XML (attributes → `@` prefix, repeated elements → arrays, namespace stripping)
- [x] Format converters — XLSX (sheet selection, header row) — via separate extension packages (`Elwood.Xlsx`, `@elwood-lang/xlsx`)
- [x] Extension/plugin API: `RegisterMethod` (.NET), `registerMethod` (TS) for optional packages
- [x] Format converters — Parquet (read/write via extension: `Elwood.Parquet`, `@elwood-lang/parquet`)
- [x] Binary pass-through: CLI `--input-format binary` reads files as base64
- [x] Multi-format test inputs: test runners support `input.csv`, `input.txt`, `input.xml`
- [x] Parser fix: `$.method()` resolves correctly when `$` is a non-object value
- [x] `.parseJson()` general-purpose method + `fromCsv({ parseJson: true })` convenience option
- [x] Bracket property access: `obj["@attr"]` for XML attributes and special-character keys
- [x] `.first()` / `.last()` on strings return first/last character
- [x] Extension exceptions wrapped as diagnostics (not raw crashes)
- [x] 86 conformance test cases, 137 .NET tests (115 core + 15 CLI + 7 Parquet), 144 TS tests (140 core + 4 XLSX)

---

## Phase 2b — Performance ✅

**Goal:** Ensure Elwood outperforms comparable JSONPath-based transformation engines.

### Result: .NET interpreter is 2x faster than legacy baseline

Benchmarked in-process on 100K rows (fair comparison, same machine, no HTTP overhead):

| Test | Elwood .NET | Legacy baseline | Speedup |
|---|---|---|---|
| `where active \| select name` | 121ms | 240ms | **2.0x faster** |
| `select` with toString + charArray concat | 836ms | 1,819ms | **2.2x faster** |

### Why the interpreter is fast enough
- **Lazy evaluation** via `LazyArrayValue` streams items through pipeline stages without materializing intermediate arrays
- **LINQ integration** — .NET's optimized `Where`/`Select`/`Take` on `IEnumerable` avoids per-item allocation
- The JIT compiler already optimizes the hot interpreter loop after warm-up

### Compiled mode (explored, removed)
Expression Tree compilation was implemented and tested but provided no speedup over the interpreter. The interpreter's lazy streaming is more efficient than compiled fused loops that materialize arrays. The compiled mode was removed to reduce complexity.

### TypeScript performance
Benchmarked on 100K rows — the TS interpreter is already **~5x faster than .NET**:

| Test | .NET | TypeScript |
|---|---|---|
| where+select name | 121ms | 24ms |
| toString + charArray concat | 836ms | 173ms |

V8's JIT aggressively optimizes native array methods (`filter`, `map`). Generator-based lazy evaluation is unnecessary — the eager approach with V8-optimized array ops is faster.

### Future optimization (if needed)
- [ ] Bypass `IElwoodValue` abstraction for direct `JsonNode` access in .NET hot paths — only if 100MB+ workloads need it

---

## Phase 3 — Integration Pipeline Configuration

**Goal:** Use YAML to describe complete data integration pipelines — sources, transformations, and destinations — in a single document. YAML handles **declarative orchestration** (triggers, connections, destinations); Elwood scripts (`.elwood` files) handle **transformation logic**.

### Design: hybrid approach

Real-world integration configs embed complex expressions in many YAML values — multi-line filter chains, conditional logic, method chains, etc. This creates the same two-syntax-in-one-file problem we rejected for transformation maps.

**Solution:** YAML for structure, external `.elwood` scripts for any non-trivial expression.

**Inline in YAML (OK):**
- Static values: `trigger: http`, `concurrency: 100`, `container: output`
- Simple paths: `$.request.season`, `$.code`
- Short interpolation: `` `{$.request.season}-images` ``

**External .elwood file (required for):**
- Anything with pipes (`|`)
- Conditionals (`if`/`then`/`else`)
- Method chains (`.toUpper().split()...`)
- Filters (`where`, `in`)
- Multi-line logic

**Guideline (not enforced):** simple `$.field`, short interpolation, or brief expressions stay inline. Complex logic with multiple pipes, conditionals, or long method chains goes in an external `.elwood` file. This is a recommendation — short pipes like `$.items[*] | take(5)` are fine inline. A future `elwood validate` command could warn (not error) when inline expressions exceed a complexity threshold.

### Example

```yaml
# pipeline.elwood.yaml
version: 2

sources:
  - name: api-trigger
    trigger: http
    endpoint: /api/data/{category}
    contentType: json
    map: request-map.elwood                # ← external script

  - name: file-source
    trigger: pull
    from:
      fileShare:
        connectionString: ${FILE_SHARE_CONN}
        path: /{$.request.category}/data   # ← simple interpolation, OK inline
    map: source-transform.elwood           # ← external script

join:
  path: $
  keys: []

outputs:
  - name: publish-to-fileshare
    path: filter-results.elwood            # ← complex filter → external script
    outputId: output-id.elwood             # ← method chains → external script
    contentType: $.contentType             # ← simple path, OK inline
    concurrency: 100                       # ← static value
    map: output-map.elwood                 # ← external script (reusable!)
    destinations:
      fileShare:
        - connectionString: ${FS_CONN}
          filename: output-filename.elwood # ← complex interpolation → external

  - name: publish-to-sftp
    path: filter-results.elwood            # ← same filter, reused!
    outputId: output-id.elwood             # ← same ID logic, reused!
    contentType: $.contentType
    concurrency: 50
    map: output-map.elwood                 # ← same map, reused!
    destinations:
      sftp:
        - connectionString: ${SFTP_CONN}
          filename: output-filename.elwood # ← same filename logic, reused!
```

External scripts are **reusable** — the same `output-id.elwood` and `filter-results.elwood` are shared across multiple outputs. They're also **independently testable** via the CLI.

### What stays in YAML vs external scripts

| In YAML (inline) | Inline Elwood (simple) | External .elwood (complex) |
|---|---|---|
| `trigger: http` | `path: $.results[*]` | `path: filter-active.elwood` |
| `contentType: json` | `contentType: $.contentType` | `outputId: generate-id.elwood` |
| `concurrency: 100` | `` endpoint: /api/{$.category} `` | `map: transform.elwood` |
| `container: output` | `filename: /{$.code}.json` | `filename: build-path.elwood` |

Guideline: **static config → plain YAML. Simple expressions → inline. Complex logic → external `.elwood` file.** This is a best practice, not enforced. Short inline pipes are fine when readable.

### Tasks

**Runtime + CLI Executor:**
- [ ] Define integration YAML schema (sources, outputs, destinations, join, notifications)
- [ ] YAML parser that resolves `.elwood` file references and evaluates them
- [ ] Inline Elwood expression evaluation for simple dynamic values
- [ ] Pipeline Execution State JSON schema (v1) — metadata + refs, not payloads
- [ ] `IStateStore` + `IDocumentStore` interfaces
- [ ] `InMemoryStateStore` + `InMemoryDocumentStore` implementations
- [ ] Pipeline step graph builder (ordering, fan-out, merge)
- [ ] CLI Executor (sequential, in-process)
- [ ] `elwood status` — read and display execution state
- [ ] Validation tooling: `elwood validate pipeline.elwood.yaml`

**Deployment:**
- [ ] `elwood deploy` command — uploads pipeline YAML + .elwood scripts to pipeline store
- [ ] Infrastructure is provisioned once, runs all pipelines (not per-pipeline)

**Executors (separate packages):**
- [ ] IExecutor, ISource, IDestination interfaces for pluggable executors
- [ ] Azure Executor (Functions + ASB + Storage) — separate package
- [ ] `FileSystemStateStore` + `FileSystemDocumentStore` for persistent local state

**Infrastructure (separate repo: `elwood-infra`):**
- [ ] Terraform module: Azure (Function App + ASB + Storage + App Insights)
- [ ] Terraform module: AWS (Lambda + SQS + DynamoDB + S3)
- [ ] Terraform module: Kubernetes
- [ ] Example configurations (minimal, production)

---

## Phase 4 — Developer Tooling & Ecosystem

**Goal:** IDE support, developer tools, and community ecosystem.

### IDE support
- [ ] VS Code extension: syntax highlighting for `.elwood` and `.elwood.yaml` files
- [ ] Language server (LSP): autocomplete, go-to-definition for `let` bindings, error squiggles
- [ ] Hover documentation for built-in methods
- [ ] Snippet library for common patterns

### Developer tools
- [ ] Playground multi-format expansion: input/output format selectors, conversion preview, dual-view output (see playground-spec.md Phase 2+ section)
- [ ] Trace mode: `elwood run --trace` shows step-by-step pipeline execution with intermediate values
- [ ] Visual debugger: step through pipe stages, inspect values at each step
- [ ] Schema inference: analyze an Elwood expression and infer the expected input/output JSON schema

### Ecosystem
- [ ] Documentation site (GitHub Pages or similar)
- [ ] Example library: real-world transformation patterns
- [ ] Community contributions: custom function plugins

---

## Phase 5 — Elwood DB

**Goal:** A JSON database where you store documents and query them with Elwood. No predefined schema, no document size limits. PostgreSQL as the storage backend.

**Detailed design:** See [`docs/architecture-vision.md`](architecture-vision.md) Phase 5 Vision section.

### What makes it unique
- **Elwood as the native query language** — the same language for querying and transforming, no context switch
- **No document size limits** — automatic splitting handles 100MB+ documents
- **Schema-on-write** — define structure when storing, not upfront
- **PostgreSQL backend** — we build a query translation layer, not a database engine

### Storage
- Fixed schema: 2 tables (`collections` + `chunks`), no dynamic table creation
- Large documents are split along a configured path (e.g., `$.orders[*]` → one row per order)
- PostgreSQL JSONB indexing (GIN + expression indexes) handles query performance
- Elwood queries translate to SQL, with non-SQL operations (`.toUpper()`, complex `select`) executed in Elwood on the result set

### Tasks
- [ ] `Elwood.Db` project (separate repo: `elwood-db`)
- [ ] PostgreSQL schema (collections + chunks tables)
- [ ] Collection management (`elwood db create`, `elwood db drop`)
- [ ] Document storage with automatic splitting
- [ ] Index management (translate user-defined paths to PostgreSQL expression indexes)
- [ ] Query translation: Elwood `where` → SQL `WHERE`, `take`/`skip` → `LIMIT`/`OFFSET`, `orderBy` → `ORDER BY`
- [ ] Aggregation push-down (`count`, `sum`, `min`, `max` → SQL)
- [ ] CLI integration: `elwood db query`, `elwood db store`
- [ ] REPL integration: `:db connect`, `:db use`
- [ ] Optional SQLite backend for embedded/dev use

---

## Execution Order

```
Phase 1   ✅  Build the .NET engine
   ↓
Phase 1b  ✅  Lazy evaluation (.NET) → repo restructure → TypeScript port
   ↓
Phase 1c  ✅  Publish everything (GitHub, NuGet, npm, CI, Playground, API container)
   ↓
Phase 2   ✅  Multi-format I/O (fromCsv, toXml, etc.) + script-based maps
   ↓
Phase 2b  ✅  Performance — 2x faster than legacy baseline (compiled mode explored, not needed)
   ↓
Phase 3       Integration pipeline configuration (Elwood Runtime + Executors)
   ↓
Phase 4       IDE support, developer tools, ecosystem
   ↓
Phase 5       Elwood DB — JSON database with Elwood queries (separate repo)
```

Phases 2b and 4 span both .NET and TypeScript implementations.
Phase 5 is a separate project building on Elwood Core.

## Adoption Strategy

Elwood is designed for **incremental adoption**:

1. **Phase 1** (done): Standalone transformation engine. Use via CLI or .NET library.
2. **Phase 1b** (done): Cross-platform reach. Use in browsers, Node.js, edge runtimes.
3. **Phase 1c** (done): Open-source launch. Available via NuGet, npm, browser playground, and self-hosted API container.
4. **Phase 2** (done): Multi-format I/O. All formats complete. Extension API for XLSX. CLI format flags.
5. **Phase 2b** (done): Performance verified — 2x faster than legacy baseline on 100K rows. Compiled mode explored but interpreter is already optimal.
6. **Phase 3**: Integration pipelines. YAML defines sources, transforms, and destinations. Pluggable executors run them.
7. **Phase 4**: IDE support, developer tools, and community ecosystem.
8. **Phase 5**: Elwood DB. Store JSON, query with Elwood. PostgreSQL backend, no size limits.

Each phase is independently useful. No phase requires adopting a later one.

## How to use Elwood

```
{elwood}

  As a library       →  NuGet: Elwood.Core / Elwood.Json
                        npm: @elwood-lang/core

  As a CLI tool      →  Download from GitHub Releases (Windows, macOS, Linux)
                        Or: dotnet tool install --global Elwood.Cli

  In the browser     →  Playground: https://max-favilli.github.io/elwood/

  As an API          →  docker run -p 8080:8080 ghcr.io/max-favilli/elwood-api
                        POST /api/evaluate { script, input } → result
                        (integrates with any iPaaS via HTTP)
```
