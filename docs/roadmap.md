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
- [ ] `Elwood.Api` project — minimal ASP.NET API (~50 lines): `POST /api/evaluate` with `{ script, input }` → result
- [ ] Dockerfile (multi-stage, Alpine-based, Native AOT for small image ~20MB)
- [ ] Publish container image to GitHub Container Registry (`ghcr.io/max-favilli/elwood-api`)
- [ ] GitHub Actions workflow to build and push container on release tags
- [ ] Usage: `docker run -p 8080:8080 ghcr.io/max-favilli/elwood-api`
- [ ] Enables integration with any iPaaS (MuleSoft, SnapLogic, Boomi, SAP CPI, Logic Apps, etc.) via HTTP

### Remaining engine work
- [ ] Identify and port any remaining common JSON transformation functions not yet covered

---

## Phase 2 — YAML Transformation Documents

**Goal:** Define a YAML-based document format for declarative data transformations powered by Elwood expressions, supporting multiple input and output formats.

### Design
The YAML document structure mirrors the desired output shape. Each leaf value is an Elwood expression. Elwood handles format conversion automatically — the transformation always works with JSON internally.

```
any format ──→ [input conversion] ──→ JSON ──→ Elwood transform ──→ JSON ──→ [output conversion] ──→ any format
```

```yaml
version: 2
input: json                          # json (default), csv, xml, xlsx, text
output: json                         # json (default), csv, xml, text

let:
  allSystems: $.Systems.results[*] | where s => s.Status.Value != "Retired"

map:
  systems: |
    allSystems | select s => {
      id: s.Title.toLower(),
      label: s.Title,
      domain: s.Domain.Value
    }
  metadata:
    generatedAt: now("yyyy-MM-ddTHH:mm:ss")
    systemCount: map.systems | count
```

### Format conversion

Elwood operates on JSON internally. Input conversion transforms source data to JSON; output conversion transforms JSON results to the target format.

| Format | Input (→ JSON) | Output (JSON →) | Notes |
|---|---|---|---|
| **JSON** | Native (no conversion) | Native (no conversion) | Default |
| **CSV** | Rows → array of objects, headers → property names | Array of objects → rows, property names → headers | Configurable delimiter, quoting |
| **XML** | Elements → objects, attributes → properties | Objects → elements | Configurable conventions |
| **XLSX** | Sheet → array of objects (like CSV) | Array of objects → sheet | Sheet name configurable |
| **Text** | Entire content as string, or line-split to array | Join array/render string | Line delimiter configurable |

Input conversion options in YAML:
```yaml
# CSV with custom settings
input:
  format: csv
  delimiter: ";"
  hasHeaders: true

# XML with options
input:
  format: xml
  rootElement: data

# XLSX with sheet selection
input:
  format: xlsx
  sheet: "Sheet1"          # or sheet index
```

### Tasks
- [ ] Add `Elwood.Yaml` project (depends on YamlDotNet)
- [ ] YAML document parser: `version`, `input:`, `output:`, `let:`, `map:` sections
- [ ] `map:` tree = output JSON shape; leaf values = Elwood expressions
- [ ] Inline expressions (single-line values) and block expressions (`expr: |`)
- [ ] Self-referencing: `map.systems` references already-computed siblings (topological sort)
- [ ] External map references: `map: some-file.elwood.yaml` (load from file)
- [ ] CLI support: `elwood run transform.elwood.yaml --input data.csv`
- [ ] Input format converters:
  - [ ] CSV → JSON (configurable delimiter, headers, quoting)
  - [ ] XML → JSON (configurable element/attribute mapping)
  - [ ] XLSX → JSON (sheet selection, header row)
  - [ ] Text → JSON (line splitting, raw string)
- [ ] Output format converters:
  - [ ] JSON → CSV
  - [ ] JSON → XML
  - [ ] JSON → Text

---

## Phase 3 — Integration Pipeline Configuration

**Goal:** Extend the YAML format to describe complete data integration pipelines — sources, transformations, and destinations — in a single document.

This enables Elwood to serve as a configuration language for data pipeline orchestration tools, not just a standalone transformation engine.

### Design
One `.elwood.yaml` file per integration pipeline:

```yaml
version: 2

sources:
  - name: api-trigger
    trigger: http
    endpoint: /api/data/{category}
    contentType: json
    map:
      request:
        category: $.category

  - name: file-source
    trigger: pull
    from:
      fileShare:
        connectionString: ${FILE_SHARE_CONN}
        path: /{$.request.category}/data
    map: source-transform.elwood.yaml

join:
  path: $
  keys: []

outputs:
  - name: publish-results
    path: $.results[*]
    outputId: `{$.code}_{$.fileName}`
    contentType: $.contentType
    concurrency: 100
    map:
      code: $.code
      fileName: $.fileName
    destinations:
      blobStorage:
        - connectionString: ${CDN_CONN}
          container: output
          filename: /{$.code}/{$.fileName}
```

### Tasks
- [ ] Define integration YAML schema (sources, outputs, destinations, join, notifications)
- [ ] Elwood expression evaluation for dynamic values (`outputId`, paths, filenames, etc.)
- [ ] Inline maps (YAML tree) and external map file references
- [ ] Source format conversion (xml/csv/xlsx → JSON)
- [ ] Validation tooling: `elwood validate pipeline.elwood.yaml`

### What stays declarative vs Elwood expressions
| Declarative (plain YAML) | Elwood expressions (dynamic) |
|---|---|
| `trigger: http` | `` outputId: `{$.code}_data` `` |
| `contentType: json` | `path: $.results[*]` |
| `concurrency: 100` | `filename: /{$.code}/{$.fileName}` |
| `container: output` | Any `map:` section |

Rule: **if a value depends on data at runtime, it's an Elwood expression. If it's static infrastructure config, it's plain YAML.**

---

## Phase 4 — Advanced

**Goal:** Performance optimization, developer tooling, and ecosystem.

### Compiled mode

The tree-walk interpreter with lazy evaluation is production-viable for large documents. Compiled mode eliminates interpretation overhead entirely for maximum performance.

#### .NET — Expression Trees / IL emit
- [ ] Compile Elwood AST to .NET Expression Trees → JIT-compiled delegates
- [ ] IL emit for hot paths (frequently evaluated expressions)
- [ ] Cache compiled expressions for reuse (same transform applied to thousands of records)
- [ ] Benchmark suite: compare tree-walk vs compiled on 100MB documents
- [ ] Target: near-native speed

#### TypeScript — code generation
- [ ] Compile Elwood AST to generated JavaScript functions (`new Function()` / code-gen)
- [ ] V8/SpiderMonkey JIT optimizes the generated code (inlining, hidden classes, loop unrolling)
- [ ] Cache compiled functions for repeated execution
- [ ] Benchmark suite: compare tree-walk vs compiled on representative workloads

#### Expected performance progression
```
Hand-written switch loop (baseline)     ██████████  reference point
Tree-walk interpreter                   ████        ~2-5x slower
Tree-walk + lazy evaluation             ██████      ~1.5-2x slower (less memory/GC)
Compiled mode                           ███████████ potentially faster than baseline
```

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

## Execution Order

```
Phase 1  ✅  Build the .NET engine
   ↓
Phase 1b      Lazy evaluation (.NET) → repo restructure → TypeScript port
   ↓
Phase 1c      Publish everything (GitHub, NuGet, npm, CI, Playground, API container)
   ↓
Phase 2       YAML transformation documents + multi-format I/O
   ↓
Phase 3       Integration pipeline configuration (Elwood Runtime)
   ↓
Phase 4       Compiled mode, IDE, Playground multi-format expansion
```

Phase 4 spans both .NET and TypeScript implementations.

## Adoption Strategy

Elwood is designed for **incremental adoption**:

1. **Phase 1** (done): Standalone transformation engine. Use via CLI or .NET library.
2. **Phase 1b** (done): Cross-platform reach. Use in browsers, Node.js, edge runtimes.
3. **Phase 1c**: Open-source launch. Available via NuGet, npm, browser playground, and self-hosted API container.
4. **Phase 2**: Declarative YAML maps. Describe transformations as documents, not code.
5. **Phase 3**: Full pipeline configuration. One YAML file describes sources, transforms, and destinations.
6. **Phase 4**: Performance and tooling make Elwood production-ready for the most demanding workloads.

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
