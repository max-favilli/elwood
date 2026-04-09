# Changelog

## 2026-04-09 — Phase 3 Step 6b: GitPipelineStore

Git-backed pipeline store. Every save is a git commit, revision history comes from `git log`, restore checks out files at a previous revision and commits the result.

### What's new
- **`GitPipelineStore`** — implements `IPipelineStore`, wraps `FileSystemPipelineStore` for file I/O and adds git operations via `GitHelper`
- **`GitHelper`** — thin wrapper around the git CLI. Uses `Process.Start("git", ...)` rather than LibGit2Sharp to avoid native binary compatibility issues on newer .NET versions. The same approach used by Azure DevOps Pipelines, GitHub Actions, and Terraform.
- **11 tests** exercising: save/get round-trip, list with filter, delete + commit, revision history ordering + limits, restore to older revision (including script add/removal), author recording in commits, invalid revision handling, empty repo safety

### Design decisions
- **Git CLI over LibGit2Sharp:** LibGit2Sharp has chronic native binary issues on .NET 8+/10+ and arm64. The git CLI is always available on servers and CI runners.
- **Store doesn't manage remotes/push/pull:** The API server (6e) handles webhook-triggered `git pull` and optional `git push`. The store is concerned only with local commits.
- **Wraps FileSystemPipelineStore:** Read operations (List, Get) are delegated directly. Write operations (Save, Delete) write files via the FS store, then stage + commit.

### Files modified
- `dotnet/src/Elwood.Pipeline/Registry/GitPipelineStore.cs` (NEW)
- `dotnet/src/Elwood.Pipeline/Registry/GitHelper.cs` (NEW)
- `dotnet/tests/Elwood.Pipeline.Tests/GitPipelineStoreTests.cs` (NEW — 11 tests)
- `docs/roadmap.md` (mark Step 6b complete)
- `docs/changelog.md`

## 2026-04-08 — Phase 3 Step 6a: pipeline modes + Azure storage adapters (v0.4.0)

First slice of Phase 3 Step 6 (Cloud Executors). Lays the foundation for production cloud deployment by adding:

1. **Pipeline mode + response output** in the YAML schema, validated at parse time.
2. **`Elwood.Pipeline.Azure`** opt-in NuGet package with Redis state, Blob document, and Redis registry adapters.
3. **24 integration tests** using Testcontainers (real Redis + Azurite).
4. **CI fix:** the existing `ci.yml` was only running `Elwood.Core.Tests`. Now runs all 6 .NET test projects (Core, Pipeline, CLI, Parquet, Pipeline.Azure) on every push.

### Schema additions

```yaml
mode: sync                       # default — runs in HTTP function lifetime, returns one output to caller
                                 # OR mode: async — fans out via queue, returns 202 + execution ID

outputs:
  - name: api-response
    response: true               # required exactly once when mode is sync, forbidden when async
    map: build-response.elwood
  - name: log-to-blob
    destinations:
      blob:
        - container: audit-log   # side effect — not returned
```

Validation rules (enforced by `PipelineParser.ValidateConfig`):
- `mode` must be "sync" or "async" (case-insensitive)
- Sync mode requires exactly one output with `response: true`
- Async mode forbids `response: true` on any output
- Default mode is "sync" if omitted

Two helper properties on `PipelineConfig`: `IsSyncMode` and `ResponseOutput`.

### Storage adapters (`Elwood.Pipeline.Azure`)

| Adapter | Backs | Notes |
|---|---|---|
| `RedisStateStore` | `IStateStore` | Per-step updates use Lua scripts for atomic load → mutate → save with `KEEPTTL`. Default TTL: 3 days. Concurrent fan-out workers can update the same execution without lost updates. |
| `BlobDocumentStore` | `IDocumentStore` | Blob name = document key. Container auto-created. 404 → null. Lifecycle delegated to Azure Blob lifecycle policies. |
| `RedisPipelineRegistry` | `IPipelineRegistry` | Two constructors: read-only for executors, writable for the API server. Literal route matching only in 6a (parameter extraction deferred to 6d). |

DI helpers: `AddElwoodAzureStorage(opts => ...)` + `AddElwoodAzureWritablePipelineRegistry(...)`.

### Test strategy (no local Docker required)

- **Unit tier** runs everywhere — `dotnet test --filter "Category!=Integration"` builds the new project but skips its tests
- **Integration tier** runs on CI (`ubuntu-latest` has Docker pre-installed) and locally if you have Docker
- All `Elwood.Pipeline.Azure.Tests` are marked `[Trait("Category", "Integration")]`
- The concurrent-writers test (`UpdateSourceStep_ConcurrentWriters_NoLostUpdates`) fires 20 parallel updates against the same execution and asserts all 20 sources persist — proves Lua atomicity. Will fail loudly if anyone "simplifies" to read-modify-write.
- The `KEEPTTL` test verifies the Lua script preserves the original expiration across updates.

### Versions bumped to 0.4.0

| Package | 0.3.0 → 0.4.0 |
|---|---|
| `Elwood.Core` | ✓ |
| `Elwood.Json` | ✓ |
| `Elwood.Pipeline` | ✓ |
| `Elwood.Cli` | ✓ |
| `Elwood.Xlsx` | ✓ |
| `Elwood.Parquet` | ✓ |
| `Elwood.Pipeline.Azure` | new — first publish at 0.4.0 |
| `@elwood-lang/core` | ✓ |
| `@elwood-lang/xlsx` | ✓ |

### CI/release workflow changes

- `ci.yml`: build step unchanged, test step now runs the entire solution (`dotnet test dotnet/Elwood.slnx`) instead of only `Elwood.Core.Tests`. **Pre-existing gap** — Pipeline, CLI, Parquet test projects were never run on CI before this change. They are now.
- `release.yml`: pack/push commands extended with `Elwood.Pipeline.Azure`. Same `--skip-duplicate` pattern as the rest.

### Files modified

```
NEW:
  dotnet/src/Elwood.Pipeline.Azure/                   (5 files: csproj + 4 .cs)
  dotnet/tests/Elwood.Pipeline.Azure.Tests/           (6 files: csproj + 2 fixtures + 3 test files)

MODIFIED:
  dotnet/src/Elwood.Pipeline/Schema/PipelineConfig.cs (Mode + Response + helpers)
  dotnet/src/Elwood.Pipeline/PipelineParser.cs        (ValidateConfig + Parse wiring)
  dotnet/src/Elwood.Core/Elwood.Core.csproj           (0.3.0 → 0.4.0)
  dotnet/src/Elwood.Json/Elwood.Json.csproj           (0.3.0 → 0.4.0)
  dotnet/src/Elwood.Pipeline/Elwood.Pipeline.csproj   (0.3.0 → 0.4.0)
  dotnet/src/Elwood.Cli/Elwood.Cli.csproj             (0.3.0 → 0.4.0)
  dotnet/src/Elwood.Xlsx/Elwood.Xlsx.csproj           (0.3.0 → 0.4.0)
  dotnet/src/Elwood.Parquet/Elwood.Parquet.csproj     (0.3.0 → 0.4.0)
  dotnet/Elwood.slnx                                  (added 2 new projects)
  dotnet/tests/Elwood.Pipeline.Tests/PipelineTests.cs        (response: true on inline YAML)
  dotnet/tests/Elwood.Pipeline.Tests/SyncExecutorTests.cs    (response: true on 7 inline YAMLs)
  dotnet/tests/Elwood.Pipeline.Tests/ModeValidationTests.cs  (NEW — 10 tests)
  spec/pipelines/01-single-source-json/pipeline.elwood.yaml  (added response: true)
  spec/pipelines/02-multi-source-merge/pipeline.elwood.yaml  (added response: true)
  spec/pipelines/03-xml-source-csv-output/pipeline.elwood.yaml (added response: true)
  spec/pipelines/04-fan-out-enrichment/pipeline.elwood.yaml  (added response: true)
  spec/pipelines/05-depends-chain/pipeline.elwood.yaml       (added response: true)
  ts/package.json                                     (0.3.0 → 0.4.0)
  ts/package-lock.json
  ts-xlsx/package.json                                (0.3.0 → 0.4.0)
  ts-xlsx/package-lock.json
  .github/workflows/ci.yml                            (run full solution tests)
  .github/workflows/release.yml                       (pack/push Pipeline.Azure)
  docs/pipeline-yaml-reference.md                     (document mode + response)
  docs/roadmap.md                                     (mark Step 6a complete)
  docs/changelog.md
```

## 2026-04-07 — v0.3.0 npm follow-up

The first `v0.3.0` workflow run shipped all six NuGet packages successfully but the npm publish job failed because `ts/package.json` was still at `0.2.0` (already on npm). This follow-up bumps the npm packages and adds `@elwood-lang/xlsx` to the workflow alongside `@elwood-lang/core`.

### Published to npm
- `@elwood-lang/core` 0.3.0
- `@elwood-lang/xlsx` 0.3.0 (newly added to workflow — was never auto-published before)

### Not published — `@elwood-lang/parquet`
The npm Parquet extension cannot ship in its current form. The only mature JS Parquet reader (`hyparquet`) is async-only, but Elwood's TS extension API in `ts/src/extensions.ts` is synchronous. Use `Elwood.Parquet` (.NET) for Parquet I/O. Tracked in `docs/known-issues.md`.

### Release workflow fixes
- Move `NPM_TOKEN` env to job level (same fix pattern as `NUGET_API_KEY`)
- Add `@elwood-lang/xlsx` install/build/publish steps
- Comment in workflow explains why `ts-parquet` is excluded

### Files modified
- `.github/workflows/release.yml`
- `ts/package.json`
- `ts/package-lock.json`
- `ts-xlsx/package.json`
- `ts-xlsx/package-lock.json`
- `docs/known-issues.md`
- `docs/changelog.md`

## 2026-04-07 — v0.3.0 NuGet release (Phase 1c complete)

All Elwood packages published to nuget.org. Closes the last open item in Phase 1c (publishing was previously blocked on a locked NuGet account).

### Packages published

| Package | Version | Notes |
|---|---|---|
| `Elwood.Core` | 0.3.0 | Engine, multi-target net8.0;net10.0 |
| `Elwood.Json` | 0.3.0 | System.Text.Json adapter |
| `Elwood.Pipeline` | 0.3.0 | Pipeline YAML parser + executor (new) |
| `Elwood.Cli` | 0.3.0 | `dotnet tool install --global Elwood.Cli` |
| `Elwood.Xlsx` | 0.3.0 | XLSX format extension |
| `Elwood.Parquet` | 0.3.0 | Parquet format extension |

### Release workflow fixes
- Moved `NUGET_API_KEY` env to job level so the `if:` guard on the push step works correctly
- Added pack/push commands for `Elwood.Pipeline`, `Elwood.Xlsx`, `Elwood.Parquet` (previously the workflow only published Core/Json/Cli)
- Pipeline is packed and pushed before Cli so the dotnet tool can resolve its dependency

### Files modified
- `.github/workflows/release.yml`
- `dotnet/src/Elwood.Core/Elwood.Core.csproj`
- `dotnet/src/Elwood.Json/Elwood.Json.csproj`
- `dotnet/src/Elwood.Cli/Elwood.Cli.csproj`
- `dotnet/src/Elwood.Xlsx/Elwood.Xlsx.csproj`
- `dotnet/src/Elwood.Parquet/Elwood.Parquet.csproj`
- `docs/roadmap.md`
- `docs/changelog.md`

## 2026-03-24 — Performance benchmarks (Phase 2b complete)

Benchmarked Elwood against a legacy JSONPath-based transformation engine on 100K rows (fair in-process comparison):

| Test | Elwood .NET | Legacy baseline | Elwood TS |
|---|---|---|---|
| where+select name | 121ms | 240ms | 24ms |
| toString + charArray concat | 836ms | 1,819ms | 173ms |

- .NET interpreter is **2x faster** than legacy baseline thanks to lazy evaluation via `LazyArrayValue`
- TypeScript interpreter is **~5x faster than .NET** — V8's JIT aggressively optimizes native array methods
- Expression Tree compilation was explored but removed — the interpreter's lazy streaming outperforms compiled fused loops
- CLI integration tests added (15 tests)

## 2026-03-24 — Parquet extension + binary pass-through (full format parity)

All common data integration content types are now supported in Elwood.

- **`Elwood.Parquet`** (.NET) — fromParquet/toParquet using Parquet.Net, all types + compression
- **`@elwood-lang/parquet`** (npm) — fromParquet (read-only) using hyparquet
- CLI `--input-format binary` reads files as base64 (auto-detects .pdf, .png, .parquet, etc.)

## 2026-03-23 — CLI format flags (Phase 2 complete)

Added `--input-format` and `--output-format` flags to the CLI, completing Phase 2.

- `--input-format csv|txt|xml` — override input format (auto-detected from file extension by default)
- `--output-format csv|txt|xml` — convert output to the specified format
- `-if` / `-of` short forms
- REPL `:load` auto-detects format from file extension
- Stdin piping respects `--input-format`

### Examples
```bash
elwood run transform.elwood --input data.csv
elwood eval "$.fromCsv() | select r => r.name" --input data.csv --output-format csv
cat data.xml | elwood eval "$.fromXml().orders" --input-format xml
```

### Files modified
- `dotnet/src/Elwood.Cli/Program.cs` — full rewrite of input/output handling

## 2026-03-23 — Extension API + XLSX support

Added a plugin/extension system that allows optional packages to register custom methods, and used it to implement XLSX (Excel) support as the first extension.

### Extension API
- **.NET**: `ElwoodEngine.RegisterMethod(name, handler)` — extensions provide `ElwoodMethodHandler` delegates
- **TypeScript**: `registerMethod(name, handler)` — global registry, extensions auto-register on import
- Extensions cannot override built-in methods — the built-in switch runs first

### XLSX Extension
- **`Elwood.Xlsx`** (.NET) — NuGet package using `DocumentFormat.OpenXml`
- **`@elwood-lang/xlsx`** (npm) — package using SheetJS (`xlsx`)
- `fromXlsx(options?)` — parse base64-encoded XLSX → array of objects
- `toXlsx(options?)` — array of objects → base64-encoded XLSX
- Options: `headers` (bool), `sheet` (name or index)
- Usage: `XlsxExtension.Register(engine)` (.NET) or `import '@elwood-lang/xlsx'` (TS)

### Files created
- `dotnet/src/Elwood.Core/Extensions/ElwoodExtensionRegistry.cs` — registry + delegate type
- `dotnet/src/Elwood.Xlsx/` — .NET XLSX extension package
- `ts/src/extensions.ts` — TS method registry
- `ts-xlsx/` — npm XLSX extension package

### Files modified
- `dotnet/src/Elwood.Core/ElwoodEngine.cs` — holds registry, exposes RegisterMethod
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — extension fallback in method dispatch
- `ts/src/evaluator.ts` — extension fallback in callBuiltin
- `ts/src/index.ts` — re-exports registerMethod
- `docs/syntax-reference.md` — fromXlsx/toXlsx docs

## 2026-03-22 — Bracket property access

Added `obj["propertyName"]` syntax for accessing properties with special characters (e.g., `@`-prefixed XML attributes).

- `b["@id"]` — access XML attribute properties from `fromXml()` output
- `obj[variable]` — dynamic property access with computed keys
- Works in both .NET and TypeScript evaluators

### Test cases added
- `86-bracket-property-access` — XML attributes accessed via bracket notation

### Files modified
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — EvaluateIndex: string index on objects
- `ts/src/evaluator.ts` — evalIndex: string index on objects
- `docs/syntax-reference.md` — bracket property access syntax

## 2026-03-22 — fromXml / toXml (Phase 2)

Added XML format conversion — the last built-in format I/O pair.

### New features
- `.fromXml(options?)` — parse XML string into navigable JSON structure
  - Attributes → `@attr` properties, repeated elements → arrays, leaf elements → strings
  - Options: `attributePrefix` (default `@`), `stripNamespaces` (default `true`)
- `.toXml(options?)` — serialize JSON object to XML string
  - Single top-level key becomes root element; arrays become repeated elements
  - Options: `attributePrefix`, `rootElement`, `declaration` (default `true`)

### Test cases added
- `83-fromxml` — parse XML with repeated elements, pipe through select
- `84-toxml` — serialize JSON with arrays to XML
- `85-fromxml-file` — real XML file as input, filter + transform pipeline

### Files modified
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — EvaluateFromXml, EvaluateToXml, XML helper methods
- `ts/src/evaluator.ts` — parseXml (zero-dependency XML parser), evalFromXml, evalToXml, XML helpers
- `docs/syntax-reference.md` — added fromXml/toXml

## 2026-03-22 — parseJson and CSV enhancements (Phase 2)

Added `.parseJson()` method for deserializing embedded JSON strings, and enhanced `fromCsv`/`toCsv` with additional options.

### New features
- `.parseJson()` — general-purpose method to deserialize a JSON string into a navigable value; returns null if invalid
- `fromCsv({ parseJson: true })` — automatically detect and parse JSON values in CSV cells
- `fromCsv({ skipRows: n })` — skip leading metadata/title rows before parsing
- `fromCsv({ headers: false })` — auto-generates alphabetic column names (A, B, C, ... Z, AA, AB) matching Excel convention
- `toCsv({ alwaysQuote: true })` — forces all fields to be quoted, useful for strict RFC 4180 compliance

### Test framework
- Test runners now support `input.csv`, `input.txt`, `input.xml` as alternatives to `input.json`
- Non-JSON input files are read as raw strings ($ = file content), enabling `$.fromCsv()` directly
- Parser fix: `$.method()` now correctly resolves as a method call when `$` is a string value (DollarDot token consumed the dot that ParsePostfix needed)

### Test cases added
- `77-fromcsv-no-headers` — skipRows + auto-generated column names
- `78-tocsv-always-quote` — alwaysQuote option
- `79-parsejson` — standalone parseJson method with navigation and null fallback
- `80-fromcsv-parsejson` — fromCsv with parseJson option for embedded JSON in cells
- `81-fromcsv-file` — real CSV file as input (input.csv instead of input.json)
- `82-fromtext-file` — real text file as input (input.txt, log filtering example)

### Files modified
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — EvaluateParseJson, EvaluateFromCsv (skipRows, auto columns, parseJson), EvaluateToCsv (alwaysQuote), CsvEscape, GetAlphabeticColumnName
- `dotnet/src/Elwood.Core/Parsing/Parser.cs` — ParsePath: detect method call on last path segment
- `dotnet/tests/Elwood.Core.Tests/FileBasedTests.cs` — support input.csv/txt/xml, string input handling
- `ts/src/evaluator.ts` — parseJson case, evalFromCsv (skipRows, auto columns, parseJson), evalToCsv (alwaysQuote), csvEscape, getAlphabeticColumnName, getOptNumber helper
- `ts/src/parser.ts` — parsePath: detect method call on last path segment
- `ts/tests/conformance.test.ts` — support input.csv/txt/xml, string input handling
- `docs/syntax-reference.md` — added parseJson, updated fromCsv/toCsv option lists

## 2026-03-22 — iterate and takeWhile

Added `iterate(seed, fn)` for generating lazy sequences and `takeWhile` pipe operator for conditional sequence limiting.

### New features
- `iterate(seed, fn)` — generates a lazy sequence: `[seed, fn(seed), fn(fn(seed)), ...]`. Must be limited by `take`, `takeWhile`, or `first`.
- `| takeWhile predicate` — takes items while predicate is true, then short-circuits. Critical for infinite sequences.
- Safety limit: iterate throws after 1,000,000 iterations (.NET) / 10,000 (TypeScript) without a limiting operator.

### Test cases added
- `69-iterate-basic` — powers of 2 with take
- `70-iterate-state` — accumulating state across iterations
- `71-takewhile` — conditional limit on infinite sequence
- `72-iterate-fibonacci` — Fibonacci sequence via iterate

### Files modified
- `dotnet/src/Elwood.Core/Syntax/Ast.cs` — TakeWhileOperation
- `dotnet/src/Elwood.Core/Parsing/Parser.cs` — takeWhile parser
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — takeWhile + iterate evaluation
- `ts/src/ast.ts`, `ts/src/parser.ts`, `ts/src/evaluator.ts` — TypeScript ports
- `docs/syntax-reference.md` — takeWhile + iterate documented

---

## 2026-03-21 — Comprehensive function library, file-based tests, full function parity, join modes

Massive expansion of built-in functions, covering all common JSON transformation operations. Added file-based test framework with 63+ test cases (each with input, expression, expected output, and explanation).

### New pipe operators
- `| concat` / `| concat separator` — join array into string (default separator `|`)
- `| index` — replace items with 0-based indices
- `| reduce (acc, x) => expr [from init]` — general-purpose fold
- `| join source on lKey equals rKey [into alias]` — hash-join two arrays (O(n+m))
- `| first pred` / `| last pred` — optional predicate for first/last matching item

### New built-in methods
- **String**: `left(n)`, `right(n)`, `padLeft(w, c)`, `padRight(w, c)`, `toCharArray()`, `regex(pattern)`, `urlDecode()`, `urlEncode()`, `sanitize()`, `concat(sep, ...arrays)`
- **String extended**: `toLower(position)`, `toUpper(position)`, `trim(chars)`, `trimStart(chars)`, `trimEnd(chars)`, `replace(s, r, caseInsensitive)`
- **Numeric**: `truncate()`, `round("awayFromZero"|"toEven")`
- **DateTime**: `dateFormat(fmt)`, `dateFormat(inputFmt, outputFmt)`, `tryDateFormat(...)`, `add(timespan)`, `toUnixTimeSeconds()`, `now(fmt, timezone)`, `utcNow(fmt)`
- **Type conversion**: `convertTo("Int32"|"Double"|"Boolean"|...)`, `boolean()`, `not()`, `toString(format)`
- **Null/empty checks**: `isNull()`, `isEmpty()`, `isNullOrEmpty()`, `isNullOrWhiteSpace()` — all with optional fallback argument
- **Object manipulation**: `clone()`, `keep(props...)`, `remove(props...)`, `in(arrays...)`
- **Collection**: `sum()`, `min()`, `max()`, `first()`, `last()`, `take(n)`, `skip(n)`, `index()`
- **Crypto**: `hash(length?)`, `rsaSign(data, key)`
- **Generators**: `range(start, count)`, `newGuid()`

### New language features
- **Spread operator**: `{ ...obj, newProp: val }` — copy object properties
- **Memo functions**: `let f = memo x => expr` — memoized functions with automatic cache by argument
- **Array slice**: `$[2:5]`, `$[:3]`, `$[-2:]` — native JSONPath slice syntax
- **Auto-mapping**: `$.items[*].name` maps property access over arrays
- **Method calls on paths**: `$.items[*].length()` — parser correctly handles `.method()` after path expressions
- **String comparison**: `<`, `>`, `<=`, `>=` work on strings (ordinal comparison)

### Test framework
- File-based test framework: triplets of `.elwood` + `.input.json` + `.expected.json` + `.explanation.md`
- 63 file-based test cases covering all features
- 25 code-based tests (including non-deterministic functions)
- 88 total tests passing

### Files modified
- `src/Elwood.Core/Parsing/Lexer.cs` — spread `...` token, `from`/`memo` keywords
- `src/Elwood.Core/Parsing/Parser.cs` — slice syntax, spread in objects, memo, reduce, method-on-path fix
- `src/Elwood.Core/Syntax/Ast.cs` — MemoExpression, ReduceOperation, ConcatOperation, SliceSegment, spread support
- `src/Elwood.Core/Evaluation/Evaluator.cs` — all new methods, memo/reduce evaluation, auto-mapping, string comparison
- `src/Elwood.Cli/Program.cs` — interactive REPL
- `docs/syntax-reference.md` — complete rewrite with all features
- `docs/changelog.md` — this file

## 2026-03-20 — Initial project scaffold and core engine

First working version of the Elwood DSL engine with parser, evaluator, System.Text.Json adapter, CLI, and 21 passing tests.

### Features
- JSONPath navigation (`$`, `$.field`, `$[*]`, `$..field`)
- KQL-style pipe operators: `where`, `select`, `selectMany`, `orderBy`, `groupBy`, `distinct`, `take`, `skip`, `batch`, `count`, `sum`, `min`, `max`, `first`, `last`, `any`, `all`, `join`, `match`
- Named lambda expressions (`u => u.field`)
- Implicit `$` context in pipe operations
- `let` bindings and `return` (script mode)
- `if`/`then`/`else` conditionals
- Pattern matching (`| match "value" => result, _ => default`)
- Object and array literal construction
- String interpolation with backticks
- Arithmetic and boolean operators
- 20+ built-in methods
- Rich error reporting with source locations and "Did you mean?" suggestions
- Interactive REPL, one-shot eval, script execution, and stdin pipe support

### Files created
- `src/Elwood.Core/` — Abstractions, Syntax, Parsing, Evaluation, Diagnostics, ElwoodEngine
- `src/Elwood.Json/` — System.Text.Json adapter
- `src/Elwood.Newtonsoft/` — placeholder for Newtonsoft.Json adapter
- `src/Elwood.Cli/` — CLI tool with REPL, eval, and run modes
- `tests/Elwood.Core.Tests/` — 21 end-to-end tests
- `docs/syntax-reference.md` — language syntax reference
- `docs/changelog.md` — this file
