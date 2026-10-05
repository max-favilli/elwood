# Changelog

## 2026-10-05 — `indexBy` for lookups by key; the repeated-scan warning covers `first` and `where`, and only real repeats (v0.7.25)

0.7.24 warned about a quadratic `any`. The map that prompted it had a second quadratic that the warning did not see:

```
let findHistory = memo name => ($slice.ProductImagesHistory | first h => h.fileName == name)
```

`first` with a predicate is a scan like `any`. Called once per file with a distinct name, the memo never hits and each call walks the history. Across 338 production maps `| first` appears in 47 and `| any`/`| all` in 18, so the warning covered the rarer shape. And where `any` had a one-token fix in `.in()`, `first`-by-key had none short of restructuring the map as a join.

### `| indexBy key`

Builds an object keyed by the selector, holding references to the rows. `index[key]` reads it.

```
let historyByName = $slice.ProductImagesHistory | indexBy h => h.fileName
... if historyByName[$.Name] == null then 'N' else if historyByName[$.Name].size != ...
```

One pass to build, constant time per lookup. Rules, chosen so that `index[k]` is a drop-in for `first x => x.key == k`:

- The **first** row with a key wins.
- A key with no entry gives `null`.
- Keys are text: a number or boolean key is read by its text, so `byId[row.id]` works with numeric ids. Consequently `7` and `"7"` are one key, which `==` would keep apart.
- A row whose key is `null` has no entry and `index[null]` is `null`. This is the one difference from `first x => x.key == null`, which matches such a row.
- For every match rather than the first: `list | groupBy x => x.key | indexBy g => g.key`, then `index[k].items`.

In TypeScript the index is an object without a prototype, so a key such as `constructor` is only ever a row. As with any object the TypeScript engine builds, number-like keys enumerate in numeric order there; this matters only if an index with such keys is returned as output rather than used for lookups.

### `obj[key]` reads a property for number and boolean keys

Indexing an object with a number fell through to array indexing, where an object counts as a one-element array: `obj[0]` returned the object itself and any other number `null`. It now reads the property named by the key's text, as a string key always did, and a `null` key gives `null`. Arrays are unaffected. This is a behaviour change, to something that had no sensible use.

### The warning counts `first`, `last` and `where`

`first`/`last` with a predicate and `where` are counted exactly as `any`/`all` are: predicate evaluations per site, summed over runs, against `ScanWarningThreshold`. The count sits inside the scan, so a memo miss is counted and a memo hit is not. The suggestion names the fix for the operator: `.in()` for `any`/`all`, `indexBy` for `first`/`last`, `groupBy` then `indexBy` for `where`.

### The warning is given only when every run scans the same collection

0.7.24 reported a site on totals alone, so this was reported as quadratic once its total passed the threshold:

```
$.orders[*] | where o => (o.lines | any l => l.qty > 5)
```

It is linear: each run scans that order's own lines. With `first` counted the mistake would be common, since `first` over a row's own child collection is ordinary. When a site crosses the threshold the engine now reads the script to decide whether the scan's input is computed from the row of the nearest enclosing lambda or implicit `$` context — following names through `let` bindings inside the lambda, and allowing for inner lambdas that reuse a name — and stays silent if it is. This happens when a warning is about to be reported, never during evaluation.

The message now reads `scans the same collection once for every row of an enclosing one`.

### Measured

8,000 files against 8,000 history entries, 5% absent, .NET 10 Release:

| | time | allocated |
|---|---|---|
| `memo name => (history \| first h => h.fileName == name)`, one call per file | 8.8 s | 19.8 GB |
| `history \| indexBy h => h.fileName`, one lookup per file | 0.03 s | 17 MB |

### Files
- `dotnet/src/Elwood.Core/Evaluation/ScanAnalysis.cs` — new: decides whether a repeated scan's input depends on the enclosing row
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — `indexBy`; `obj[key]` for number/boolean keys; `first`/`last`/`where` counted; warnings filtered by the analysis
- `dotnet/src/Elwood.Core/Syntax/Ast.cs`, `Parsing/Parser.cs` — `IndexByOperation`
- `dotnet/src/Elwood.Core/ElwoodEngine.cs` — hands the parsed script to the evaluator
- `ts/src/scan-analysis.ts` — new: the same analysis
- `ts/src/evaluator.ts`, `ast.ts`, `parser.ts` — the same changes
- `playground/src/editor/elwood-language.ts` — `indexBy` highlighted and completed
- `dotnet/tests/Elwood.Core.Tests/RepeatedScanTests.cs` — new: 23 tests
- `dotnet/tests/Elwood.Core.Tests/IndexByTests.cs` — new: 20 tests
- `ts/tests/unit/repeated-scan.test.ts`, `index-by.test.ts` — new: 42 tests
- `spec/test-cases/114-index-by/` — new conformance case
- `docs/syntax-reference.md`, `docs/editor-integration-guide.md`, `README.md` — `indexBy`, repeated scans
- version 0.7.25 (Elwood.Core, Elwood.Json, `@elwood-lang/core` in lockstep)

---

## 2026-10-05 — `.in(list)` is a lookup, parsed strings are decoded once, and a repeated scan warns (v0.7.24)

A production map that diffs a 29 MB file listing against a 26 MB history spent 19 minutes and 2 TB of allocation in one expression:

```
let currentNames = (currentFiles | select $.Name)
... | where h => !(currentNames | any n => n == h.fileName)
```

`currentNames` is evaluated once — a bound pipeline is materialised at its boundary — so nothing is re-executed. The cost is the scan itself: `any` walks the list for every history row, about 2.5 billion comparisons at that size, and each comparison cost about 800 bytes and 500 ns. Two things are made cheaper here and the third is made visible: the scan in `any` itself remains, `join` or `.in()` are the way to write a membership test, and a script that does it the slow way now says so.

### `.in(list)` indexes a list that is tested repeatedly

`.in()` was also a scan. It now builds a membership index for a list the second time that same list reaches the same call site, which is what happens when the test sits inside a `where` over another collection. A list seen once is still scanned, since indexing it would cost more than the scan it replaces; so is a list that differs on every call, and an unbounded `iterate` sequence.

The index answers exactly what `==` against each element answers: strings by ordinal hash, numbers within the same 1e-10 tolerance (binary search to the neighbourhood, then the same comparison), `null` and booleans by presence, arrays and objects by serialized form. A test asserts agreement with `==` for every pairing of 22 sample values across all kinds.

The TypeScript engine indexes the strings of such a list in a `Set` and scans for other kinds. Its index is discarded at the start of each evaluation, because a host may change an input array between calls.

### Parsed strings are decoded once per value

`JsonNodeValue.Kind` probed a node for bool, double, int, long and string in turn, and `GetStringValue()` read the string again. System.Text.Json keeps a parsed string as UTF-8 and decodes it on every read, so comparing two parsed strings decoded each of them twice — over half the allocation of the comparison. `Kind` now asks the node for its kind first, and both the kind and the decoded string are kept on the wrapper. This applies to every string comparison, grouping key and method call on parsed input, not only to membership.

### A repeated scan reports itself

Nothing told the author of that map what it cost. The evaluator now counts predicate evaluations per `any`/`all` site, and when one site adds up to `ScanWarningThreshold` evaluations (default 10 million) over two or more runs, the result carries a diagnostic of severity `Warning`:

```
Warning at line 2, col 40: 'any' evaluated its predicate 16,000,000 times over 4,000 runs: it scans its input once for every row of an enclosing collection. For an equality test use value.in(list) on a let-bound list, or a join; both cost the rows plus the list rather than rows times list.
```

The result is still successful and its value is unchanged: `ElwoodResult.Success` is false only for an `Error`. A single run over a large input never warns, however large — that is linear. The totals are final ones, gathered after the result has been consumed, so a lazy `where` whose scans run while `ExecuteTo` writes the output is counted in full.

This is the first warning Elwood emits on a successful result, so a host that only reads `Diagnostics` on failure will not see it. `ElwoodEngine.ScanWarningThreshold` sets the threshold, zero disables it. In TypeScript the warning is in `result.diagnostics` with `severity: 'warning'`, and `setScanWarningThreshold(n)` sets the threshold. The CLI prints warnings to stderr, leaving stdout as the result alone; the Runtime API's script-test endpoint returns them in an `X-Elwood-Warnings` response header as a JSON array of strings.

The count is one increment per predicate evaluation and one dictionary lookup per quantifier run; no difference is measurable on the benchmark below.

### Measured

8,000 files against 8,000 history entries, 5% deleted, .NET 10 Release:

| | before | after |
|---|---|---|
| `!h.fileName.in(currentNames)` | 11.4 s, 14.1 GB | 0.04 s, 41 MB |
| `!(currentNames \| any n => n == h.fileName)` | 16.8 s, 27.5 GB | 6.7 s, 16.7 GB |
| `Kind` of a parsed string | 120 B, 164 ns | 0 B, 7 ns |
| `GetStringValue()` of a parsed string | 88 B, 88 ns | 0 B, 4 ns |

The `any` form is 2.5 times faster but still quadratic. The remaining cost per comparison is the lambda's scope (a dictionary per call) and the boolean result node; neither is changed here.

### Files
- `dotnet/src/Elwood.Core/Evaluation/MembershipIndex.cs` — new: membership index with `==` semantics
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — `.in()` indexes a list seen twice at one call site; `any`/`all` count their predicate evaluations
- `dotnet/src/Elwood.Core/Evaluation/LazyArrayValue.cs` — `iterate` sequences are marked unbounded
- `dotnet/src/Elwood.Json/JsonNodeValue.cs` — kind and decoded string resolved once per wrapper
- `ts/src/evaluator.ts` — `.in()` indexes the strings of a list tested more than once; `any`/`all` count their predicate evaluations
- `dotnet/src/Elwood.Core/ElwoodEngine.cs` — `ScanWarningThreshold`; evaluator diagnostics collected after the result is consumed
- `dotnet/src/Elwood.Cli/Program.cs` — warnings on a successful result go to stderr
- `dotnet/src/Elwood.Runtime.Api/Program.cs` — script-test endpoint returns warnings in `X-Elwood-Warnings`
- `ts/src/index.ts` — warnings in `diagnostics`; exports `setScanWarningThreshold`
- `dotnet/tests/Elwood.Core.Tests/InMembershipTests.cs` — new: 16 tests
- `dotnet/tests/Elwood.Core.Tests/ScanWarningTests.cs` — new: 11 tests
- `ts/tests/unit/in-membership.test.ts` — new: 5 tests
- `ts/tests/unit/scan-warning.test.ts` — new: 8 tests
- `spec/test-cases/113-in-membership-repeated/` — new conformance case: mixed kinds, list from a pipeline
- `docs/syntax-reference.md` — membership idiom; the quantifier warning
- version 0.7.24 (Elwood.Core, Elwood.Json, `@elwood-lang/core` in lockstep)

---

## 2026-10-03 — Streaming output: write a result without building a JSON graph (v0.7.23)

Evaluating a script produced the result twice. First `LazyValues.ToConcrete` turned the lazy result into a `JsonNode` graph, then the host called `ToJsonString()` on it to get the bytes it actually wanted. The graph is pure overhead: nobody wants it, it is a stepping stone to the text, and for a large projection it is the single biggest retained allocation of the output stage.

`ElwoodEngine.EvaluateTo` and `ExecuteTo` hand the un-materialised result to a consumer, and `ElwoodJsonWriter` in `Elwood.Json` walks it straight to a `Utf8JsonWriter`. The graph is never built.

```csharp
var output = new ArrayBufferWriter<byte>();
var result = engine.EvaluateTo(script, input, v => ElwoodJsonWriter.Write(output, v));
if (!result.Success) return BadRequest(result.Diagnostics);   // nothing sent yet
return Results.Bytes(output.WrittenMemory, "application/json");
```

### Buffer then flush, deliberately

Writing straight to a network stream would mean a failure mid-write leaves a truncated body behind a status line already sent. The consumer runs **inside** the engine's error handling, so an Elwood evaluation failure while the value is being read is reported as a diagnostic rather than thrown; other exceptions, such as an I/O failure from the writer, still propagate. Write into a buffer, check `Success`, and only then flush. Nothing reaches the wire until evaluation has succeeded.

### Measured (47.6 MB model, 80,000 records, on top of an already-parsed DOM)

| script | path | churn | retained output |
|---|---|---|---|
| project 2 fields from every record | materialise | 98 MB | 29 MB |
| project 2 fields from every record | **stream** | **79 MB** | **2 MB** |
| filter to 11,429 of 80,000 | materialise | 49 MB | 1 MB |
| filter to 11,429 of 80,000 | stream | 49 MB | 0 MB |

Retained output memory falls by 93% on the large-output case, from a 29 MB graph to 2.5 MB of bytes. Total churn falls 19%, less than the full 29 MB, because the writer's own growing buffer is allocated instead. **The win scales with output size and is nil when the output is small** — the filter case, whose output is 148 KB, is unchanged. This is a peak-memory improvement, not a throughput one: streaming was *slower* here, 304 ms against 220 ms, because encoding and escaping JSON text costs more CPU than assembling nodes. Take it where holding the graph is the problem, not to go faster.

### Correctness

The streaming path must produce exactly what the materialising path produces. Every one of the 112 conformance cases is now additionally asserted **byte-for-byte identical** between the two paths, alongside targeted tests for each value kind, property order and nesting of a grouped projection, writer options, caller-owned buffers, and the contract that a failure never invokes the consumer.

### Also

`Elwood.Runtime.Api`'s script-test endpoint now uses both this and `ParseUtf8` from 0.7.22: the request body is parsed from UTF-8 bytes rather than a string, and the response is streamed into a buffer rather than materialised and stringified. Other hosts are unchanged and can opt in when there is a measured reason to.

`Evaluate`, `Execute`, `EvaluateTo` and `ExecuteTo` now share one `Run` method, so lexing, parsing, evaluation and error handling exist once rather than four times.

### Files
- `dotnet/src/Elwood.Core/ElwoodEngine.cs` — `EvaluateTo`/`ExecuteTo`; shared `Run` pipeline
- `dotnet/src/Elwood.Json/ElwoodJsonWriter.cs` — new: walks any `IElwoodValue` to a `Utf8JsonWriter`, with a fast path for already-concrete nodes
- `dotnet/src/Elwood.Runtime.Api/Program.cs` — script-test endpoint uses `ParseUtf8` and the streaming writer
- `dotnet/tests/Elwood.Core.Tests/StreamingOutputTests.cs` — new: 124 tests, including byte-for-byte parity across the whole conformance corpus
- version 0.7.23 (Elwood.Core, Elwood.Json, `@elwood-lang/core` in lockstep — .NET-only change)

---

## 2026-10-03 — UTF-8 parse entry points: `ParseUtf8` (v0.7.22)

`IElwoodValueFactory` exposed only `Parse(string)`, so a caller holding UTF-8 bytes — a file, an HTTP body, a blob — had to decode to a UTF-16 string first. That string is roughly twice the document's size and exists before parsing even begins, which on large models is the single most expensive thing about getting data into Elwood.

Two overloads are added: `ParseUtf8(ReadOnlySpan<byte>)` and `ParseUtf8(Stream)`.

Measured on a 47.6 MB compact UTF-8 document (heap retained after a forced blocking collection with the parsed model rooted; each entry point measured in its own process, because System.Text.Json rents parse buffers from `ArrayPool` and measuring several in one process lets later ones reuse earlier ones' buffers for free):

| entry point | allocated | retained | × document size |
|---|---|---|---|
| `Parse(string)` | 399 MB | 399 MB | 8.4× |
| `ParseUtf8(bytes)` | 304 MB | 304 MB | **6.4×** |
| `ParseUtf8(stream)` | 304 MB | 304 MB | **6.4×** |

A 24% reduction in both allocation and retained memory, and about 17% faster. For reference, the same document as a Newtonsoft `JToken` graph retains 981 MB (20.6×).

### Not a breaking change

Both members ship with a **default interface implementation** that transcodes and delegates to `Parse(string)`, so an existing adapter that implements only the original members keeps compiling and working — covered by a test that asserts a string-only adapter reaches the fallback. `JsonNodeValueFactory` overrides both to call `JsonNode.Parse` on the bytes directly. A leading UTF-8 byte order mark is skipped, which the string path never had to handle.

The overloads are deliberately named `ParseUtf8` rather than overloading `Parse`, so that an existing `Parse(null)` call site cannot become ambiguous between `string` and `Stream`.

### Files
- `dotnet/src/Elwood.Core/Abstractions/IElwoodValueFactory.cs` — `ParseUtf8` span and stream overloads with default implementations; shared `StripBom` helper
- `dotnet/src/Elwood.Json/JsonNodeValueFactory.cs` — direct UTF-8 overrides
- `dotnet/tests/Elwood.Core.Tests/ParseUtf8Tests.cs` — new: 8 tests covering span/stream parity with the string path, BOM, non-ASCII, stream ownership, null, end-to-end evaluation, and the default-implementation fallback
- `docs/dotnet-integration-guide.md` — when and why to prefer `ParseUtf8`, with the measured table
- version 0.7.22 (Elwood.Core, Elwood.Json, `@elwood-lang/core` in lockstep — .NET-only change; the TypeScript engine takes JavaScript strings and has no equivalent)

---

## 2026-09-23 — Object literals hold references; intermediate cascades no longer copy rows (v0.7.21)

Follow-up to v0.7.20. Grouping itself no longer copied the dataset, but a common real-world shape still did: a `let`-bound `groupBy` cascade whose intermediate objects capture **whole rows**, for example

```
let cascade = $[*]
  | groupBy r => r.style
  | select s => {
      rep: (s.items | first r => r.exported == "Yes"),
      sizes: (s.items | groupBy r => r.sku | select k => k.items[0])
    }
```

Object literals were built through `IElwoodValueFactory.CreateObject`, which materializes a JSON node graph and therefore deep-clones every embedded value. A captured input row is not evaluator-owned, so each one was copied in full. When the rows are wide (dozens of columns) and the grouping key is close to the row identity — a per-sku dedupe keeps essentially every row — that single line reintroduces a whole copy of the dataset, even though the final projection reads only scalars off those rows and the output is a small fraction of the input.

### What changed

- **Object literals are lazy.** `EvaluateObject` now returns a `LazyObjectValue` holding references to its already-evaluated property values instead of building a node graph. Property values are still evaluated eagerly at the literal, so evaluation order and non-deterministic functions are unaffected; only materialization is deferred. An intermediate object that never reaches the output now costs a pointer per property, and reading `s.rep.someColumn` navigates straight into the original row.
- **Duplicate-key semantics preserved exactly.** A concrete JSON object resolves a repeated key — produced by a spread followed by an explicit override, or by a computed key colliding with a literal one — by keeping its **first position** and taking the **last value**. `LazyObjectValue.Create` reproduces that rule, with a fast path when there are no duplicates. This behaviour had **no test coverage** (`51-spread-operator` uses disjoint key sets), so it is now pinned by tests before the code path changed.
- **Wide lazy objects index themselves.** Property lookup is a linear scan for small objects and switches to a dictionary above eight properties, so object literals used as lookup tables do not regress.

### Measured (map allocation vs. the input graph, 6,000 rows × 60 columns)

Rows carry one distinct sku each, as in a real spreadsheet export where the sku column is the row identity.

| Map | v0.7.20 | v0.7.21 |
|---|---|---|
| `let`-bound cascade capturing whole rows | 1.13× | **0.62×** |
| three-level `groupBy` + `where`/`first` (no whole-row capture) | 0.61× | 0.61× |
| two-level `groupBy` with projections | 0.47× | 0.47× |

The ~0.51× drop is close to one entire copy of a wide-row dataset. Maps without a whole-row intermediate are unchanged, which is the expected signature of this fix.

### CI fix

The npm publish steps ran `npm publish --access public || true`, so a failed publish still reported a green job. The v0.7.20 release surfaced this: both npm publishes failed with `E404` on an expired token while the workflow reported success, and only NuGet actually received 0.7.20. The guards are removed — a failed publish now fails the job. The `if: env.NPM_TOKEN != ''` condition is kept, since skipping when no token is configured is deliberate.

### Files
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — object literals build a lazy object
- `dotnet/src/Elwood.Core/Evaluation/LazyObjectValue.cs` — `Create` with duplicate-key resolution; dictionary index for wide objects
- `dotnet/tests/Elwood.Core.Tests/LazyValueSemanticsTests.cs` — 4 new tests: spread override, spread-over-spread, computed-key collision, wide-object lookup
- `dotnet/tests/Elwood.Core.Tests/Benchmarks/GroupByAllocationBenchmark.cs` — new cascade case reproducing the whole-row-capture shape; row generator now emits one row per sku
- `.github/workflows/release.yml` — npm publish failures are no longer swallowed
- version 0.7.21 (Elwood.Core, Elwood.Json, `@elwood-lang/core` in lockstep — no TS code change; note npm has no 0.7.20, which contained no TypeScript changes either)

---

## 2026-09-23 — groupBy/batch/orderBy no longer deep-clone the dataset per stage (v0.7.20)

Grouping a large, wide dataset (thousands of rows × dozens of columns, as produced by spreadsheet sources) allocated several times the size of the input graph and ran out of memory on real-world maps with two or three `groupBy` levels. Root cause, in two parts:

1. **The System.Text.Json factory cloned everything it embedded.** `JsonNodeValueFactory.CreateArray`/`CreateObject` called `DeepClone()` on every item and property to satisfy the `JsonNode` single-parent rule — including nodes it had just built itself, so lazy values were copied twice on embedding.
2. **`groupBy`, `batch`, `orderBy` and `join` materialized their results through `CreateArray`.** For `groupBy`, every row was cloned when the group's `items` array was built, again when that array was embedded into the `{ key, items }` object, and again when the group was embedded into the result array — per grouping level. A nested `g.items | groupBy …` repeated all of it.

### What changed

- **Groups hold references.** `groupBy` returns lazy `{ key, items }` group objects (new `LazyObjectValue`) whose `items` are a `LazyArrayValue` over the original rows. Nested groupings, `count`, `first`, `where`, indexing etc. run over references; nothing is copied. `batch` yields lazy slices; `orderBy` and `join` return lazy arrays over the sorted/merged references.
- **The factory adopts evaluator-owned nodes and clones everything else.** A `JsonNodeValue` now carries an internal `IsFresh` flag set only for nodes the factory created during evaluation (literals, projections, clones). Embedding adopts a fresh, not-yet-attached node as-is; parsed input, navigated children and caller-supplied nodes are still deep-cloned. Inputs are therefore never mutated (a document root passed to the engine keeps `Parent == null`), and a row is copied at most once — at the moment it is embedded into the final output.
- **Pipeline boundaries evaluate once but keep references.** `EvaluatePipeline` used to convert every pipeline result into a cloned `JsonArray`; it now materializes the lazy array's element list without cloning. The "evaluated exactly once" guarantee for bound pipelines is preserved (e.g. `let ids = $[*] | select r => { id: newGuid() }` yields identical values wherever `ids` is reused).
- **Hosts still receive concrete JSON.** `ElwoodEngine.Evaluate`/`Execute` convert any remaining lazy array/object to a concrete `JsonNodeValue` at the boundary (`LazyValues.ToConcrete`), so CLI, runtime API, Azure Functions and pipeline executors are unaffected.

`IElwoodValue.Parent` is not read anywhere in the evaluator and Elwood has no parent-navigation syntax, so nothing depended on clones having a new parent. Semantics guarded by new tests: property order, `groupBy | select g => g.items[0]`, a group bound with `let` and reused, a shared value embedded twice, the same parsed input evaluated twice, and non-deterministic projections in bound pipelines.

### Measured (allocation of the map alone vs. the input graph, 6,000 rows × 60 columns)

| Map | before | after |
|---|---|---|
| two-level `groupBy` with per-group projections | 3.08× | **0.52×** |
| three-level `groupBy` + `where`/`first` per group | 4.14× | **0.67×** |

The 50 MB / 200K-order benchmark's memory delta halved (~105 MB → ~56 MB) at the same throughput (~35K rows/sec); `take(1)` short-circuit remains 0 ms. `GroupByAllocationBenchmark` asserts the ratio stays ≤ 1.5× so this cannot regress silently.

### Files
- `dotnet/src/Elwood.Json/JsonNodeValueFactory.cs` — attach-or-clone rule (`ToAttachableNode`); fresh graphs for lazy values built once
- `dotnet/src/Elwood.Json/JsonNodeValue.cs` — internal `IsFresh` ownership flag; construction delegates to the factory
- `dotnet/src/Elwood.Core/Evaluation/LazyObjectValue.cs` — new: reference-holding object value for groups
- `dotnet/src/Elwood.Core/Evaluation/LazyValues.cs` — new: lazy → concrete conversion at the engine boundary
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — `groupBy`/`batch`/`orderBy`/`join` return lazy reference-holding values; pipeline boundary materializes without cloning; `groupBy` keeps first-seen order and evaluates the key selector once per row (as the TS engine does)
- `dotnet/src/Elwood.Core/Evaluation/LazyArrayValue.cs` — materializing an already-list source no longer copies it
- `dotnet/src/Elwood.Core/ElwoodEngine.cs` — results converted to concrete values at the boundary
- `dotnet/tests/Elwood.Core.Tests/Benchmarks/GroupByAllocationBenchmark.cs` — new: allocation regression guard (≤ 1.5× input graph)
- `dotnet/tests/Elwood.Core.Tests/LazyValueSemanticsTests.cs` — new: 9 semantics tests for reference-holding values
- version 0.7.20 (Elwood.Core, Elwood.Json, `@elwood-lang/core` in lockstep — no TS code change, the TS engine never cloned)

---

## 2026-06-15 — Fix now(format, timezone): TS ignored the timezone (v0.7.19)

`now(format, timezone)` is meant to convert the current UTC time into the given IANA timezone, then format it. The **TypeScript** engine ignored the timezone argument entirely and formatted UTC — so `now("yyyy-MM-dd HH:mm:ss", "Europe/Berlin")` returned UTC instead of CEST/CET. (The browser playground runs the TS engine, which is where this was observed.) The `evalNow` source even carried a comment claiming timezone conversion was "not easily doable in pure JS without Intl" — it is exactly doable with `Intl.DateTimeFormat`, which performs the conversion using the runtime's tz data (full-ICU Node and all browsers).

Fixed the TS evaluator to convert via `Intl.DateTimeFormat`. Unknown timezone ids now report a diagnostic instead of silently falling back to UTC. `utcNow` is now explicitly UTC-only (any timezone argument is ignored), matching .NET.

The **.NET** engine already converted correctly (`TimeZoneInfo.ConvertTimeFromUtc`), but a bad/unknown timezone id threw an *uncaught* `TimeZoneNotFoundException` rather than a clean diagnostic. Hardened: unknown ids are surfaced as an `ElwoodEvaluationException` with the call site span. IANA ids (e.g. `Europe/Berlin`) resolve on both Linux and Windows .NET via ICU; if a runtime lacks tz data the error now says so instead of failing opaquely.

### Files
- `ts/src/evaluator.ts` — `evalNow` converts via new exported `formatDateInZone`; shared `applyDateFormat`; `utcNow` drops the timezone arg
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — `EvaluateNow` wraps tz resolution in a clear diagnostic; extracted testable `FormatInZone`
- `dotnet/src/Elwood.Core/Elwood.Core.csproj` — `InternalsVisibleTo` for the test project
- `dotnet/tests/Elwood.Core.Tests/EndToEndTests.cs` — now+timezone integration (live offset), `FormatInZone` DST-boundary theory (CEST +2 / CET +1), unknown-tz diagnostic
- `ts/tests/unit/now-timezone.test.ts` — new: parity tests (live offset, fixed-instant DST boundaries, unknown tz, utcNow ignores tz)
- `docs/syntax-reference.md` — clarify `now(format, timezone)` / `utcNow` semantics
- version 0.7.19 (Elwood.Core, Elwood.Json, `@elwood-lang/core`)

---

## 2026-06-12 — TS runtime diagnostics carry source positions; ESM-only npm package (v0.7.18)

Two fixes in the TypeScript package:

**Runtime diagnostic positions**: `execute()`/`evaluate()` returned runtime-error diagnostics with only `{ severity, message }` — no line/column. Two root causes, fixed together:
- The TS parser had the same consume-then-read span bug fixed on the .NET side in v0.7.17 — path segment spans pointed at the *next* token. Fixed the same 8 sites in `parsePath`/`parsePathSegments`.
- The TS evaluator threw plain `Error`s with no span at all. New `EvaluationError` (exported) carries `span` and `suggestion`; property-not-found errors throw it with the segment span, and a wrapper in the evaluate dispatch attaches the innermost expression's span to any runtime error thrown without one.

Diagnostics now match .NET `ElwoodDiagnostic` semantics: 1-based `line`/`column` pointing at the failing expression, with `suggestion` as a separate field (no longer concatenated into the message). Cross-engine parity test: `let foo = $.bar\n\nreturn {...}` reports line 1, col 13 in both the TS and .NET suites.

**ESM-only npm package**: `package.json` declared `"main": "dist/index.cjs"` and a `require` export condition, but the build is plain `tsc` and only emits ESM — `require('@elwood-lang/core')` threw `MODULE_NOT_FOUND` on every published version. Dropped `main` and the `require` condition (CJS never worked, so this breaks nobody) and moved `types` first in the exports map.

### Files
- `ts/src/parser.ts` — segment spans from the consumed token; `spanBetween` helper for `[`…`]` segments
- `ts/src/evaluator.ts` — new `EvaluationError` with span + suggestion; span-attaching wrapper around node dispatch
- `ts/src/index.ts` — runtime diagnostics map line/column/suggestion; export `EvaluationError`
- `ts/package.json` — ESM-only entry points, types-first exports, version 0.7.18
- `ts/tests/unit/diagnostics.test.ts` — new: 8 tests for diagnostic positions and segment spans (mirrors .NET `SpanRegressionTests`)
- `dotnet/src/Elwood.Core/Elwood.Core.csproj`, `dotnet/src/Elwood.Json/Elwood.Json.csproj` — version 0.7.18 (lockstep, no code change)

---

## 2026-06-12 — Fix parser spans on path segments: errors reported at wrong position (v0.7.17)

The .NET parser built path segment spans from the token *after* the consumed identifier (`new PropertySegment(Advance().Text, Span(Current.Span))` — `Advance()` consumes the identifier before `Current.Span` is read). Runtime errors like `Property 'bar' not found` were therefore attributed to the next token in the source. Within a line the offset was nearly invisible, but when the path ended a statement the error landed on the next statement entirely:

```
let foo = $.bar

return { x: foo }
```

reported `Error at line 3, col 1` (the `return` keyword) instead of line 1, col 13 (`bar`). The TypeScript implementation was not affected.

Fixed all 8 sites in `ParsePath`/`ParsePathSegments`: property, optional-chaining, and recursive-descent segments now carry the identifier token's own span; index and slice segments now span from `[` to `]`. The npm package is version-bumped in lockstep (no code change).

### Files
- `dotnet/src/Elwood.Core/Parsing/Parser.cs` — capture the consumed token before reading its span; new `Span(start, end)` overload for bracket segments
- `dotnet/tests/Elwood.Core.Tests/SpanRegressionTests.cs` — new: 6 tests asserting diagnostic positions (including the multi-statement script repro) and segment span positions
- `dotnet/src/Elwood.Core/Elwood.Core.csproj`, `dotnet/src/Elwood.Json/Elwood.Json.csproj`, `ts/package.json` — version 0.7.17

---

## 2026-05-27 — Replace auto-unwraps single-element array arguments (v0.7.16)

`.replace()` now auto-unwraps single-element arrays passed as search or replacement arguments. This fixes a silent failure when composing replace with pipe operations like `take(1)` that return arrays:

```
let filename = $.url.split("/").skip(6).take(1)
$.text.replace(filename, "replaced")
```

Previously this silently did nothing because `take(1)` returns a single-element array, not a string. Now the array is unwrapped to its sole element before matching. Multi-element arrays still produce no match (no error).

### Files
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — add `EvaluateReplace` with `Unwrap` helper
- `ts/src/evaluator.ts` — add `unwrap` in replace case
- `spec/test-cases/60-convertto/` — updated with replace-related assertions

---

## 2026-05-22 — Array spread syntax, boolean convertTo fix (v0.7.15)

Two features in this release:

**Array spread syntax**: Array literals now support the spread operator `...` to flatten arrays inline:
```
let a = [1, 2]
let b = [3, 4]
[...a, ...b, 5, 6]
```
Result: `[1, 2, 3, 4, 5, 6]`. Spreading a non-array value inserts it as-is. Mirrors the existing object spread `{...a, ...b}` syntax.

**Boolean convertTo fix**: `true.convertTo("Int32")` was returning `0` instead of `1`. Root cause: the value was stringified to `"true"` before numeric parsing, and `parseFloat("true")` = NaN fell back to 0. Now boolean values are detected before stringification and converted directly (`true`→1, `false`→0) for all numeric target types.

### Files
- `dotnet/src/Elwood.Core/Syntax/Ast.cs` — add `ArrayItem` record with `IsSpread` flag
- `dotnet/src/Elwood.Core/Parsing/Parser.cs` — array literal parsing checks for spread token
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — spread evaluation in `EvaluateArray`, boolean convertTo early-return
- `ts/src/ast.ts` — add `ArrayItem` interface, update `ArrayExpression`
- `ts/src/parser.ts` — array literal parsing with spread support
- `ts/src/evaluator.ts` — spread evaluation in array case, boolean convertTo early-return
- `spec/test-cases/112-array-spread/` — four test files (input, script, expected, description)
- `spec/test-cases/60-convertto/` — added `boolTrue`/`boolFalse` inputs and boolean→numeric assertions

---

## 2026-05-14 — Optional `else` in `if/then` (v0.7.14)

`if condition then expression` now works without an `else` clause — the else branch implicitly returns `null`. Useful for conditional-only transformations where the alternative is "nothing".

### Files
- `dotnet/src/Elwood.Core/Parsing/Parser.cs` — make `else` optional, emit null literal
- `ts/src/parser.ts` — same
- `docs/syntax-reference.md` — update conditionals section
- `spec/test-cases/110-111` — two test cases (true → value, false → null)

---

## 2026-05-14 — Parser fixes: `let`/`return` as pipe terminators, trailing `?` in postfix (v0.7.13)

Two parser bugs fixed:

**`let`/`return` not recognized as pipe operation terminators**: Optional-argument pipe operators (`| first`, `| last`, `| any`, `| all`, `| concat`) tried to consume `let`/`return` as a predicate/argument, requiring workaround parentheses. Added `IsAtPipeOperationEnd` / `isAtPipeEnd` helper that checks for `let` and `return` tokens.

**Trailing `?` not accepted in postfix expressions**: `obj.prop?` worked as a standalone expression but failed inside function arguments, array literals, and other nested contexts. `ParsePostfix` now consumes trailing `?` after `.name` member access.

### Files
- `dotnet/src/Elwood.Core/Parsing/Parser.cs` — add `IsAtPipeOperationEnd()`, fix `ParsePostfix` trailing `?`
- `ts/src/parser.ts` — add `isAtPipeEnd()`, fix `parsePostfix` trailing `?`
- `spec/test-cases/106-109` — four test cases

---

## 2026-05-13 — `| any` / `| all` without predicate (v0.7.12)

`| any` and `| all` now work without a predicate argument:
- `| any` — returns `true` if the collection is non-empty, `false` if empty
- `| all` — returns `true` always (vacuous truth for empty collections)

Useful after `| where` to check existence: `$.items[*] | where i => i.active | any`

### Files
- `dotnet/src/Elwood.Core/Syntax/Ast.cs` — make `QuantifierOperation.Predicate` nullable
- `dotnet/src/Elwood.Core/Parsing/Parser.cs` — add `ParseQuantifier` with optional predicate detection
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — handle null predicate in `EvaluateQuantifier`
- `ts/src/ast.ts` — make `predicate` optional on `QuantifierOperation`
- `ts/src/parser.ts` — detect missing predicate before calling `parsePipeArg()`
- `ts/src/evaluator.ts` — handle missing predicate in Quantifier case
- `spec/test-cases/100-105` — six test cases covering empty, non-empty, after-where, in-parens, backward compat
- `docs/syntax-reference.md` — update quantifier operator docs

---

## 2026-05-12 — Let-in-lambda and multi-param memo (v0.7.11)

Two language features that enable complex multi-step transformations inside pipe operations:

**Let bindings in lambda bodies**: Lambda bodies can now contain `let` bindings for intermediate calculations:
```
$.orders[*] | select o =>
  let total = o.qty * o.price
  let label = if total > 20 then "high" else "low"
  { id: o.id, total: total, label: label }
```

**Multi-param memo**: Memo functions now support multiple parameters:
```
let lookup = memo (sv, cn) => $.items[*] | first i => i.style == sv && i.color == cn
```

### Files
- `dotnet/src/Elwood.Core/Syntax/Ast.cs` — add `LetInExpression` AST node
- `dotnet/src/Elwood.Core/Parsing/Parser.cs` — add `ParseLambdaBody()` helper; use in both lambda parsing locations
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — add `EvaluateLetIn` method
- `ts/src/ast.ts` — add `LetInExpression` type to union
- `ts/src/parser.ts` — add `parseLambdaBody()` helper; use in both lambda parsing locations
- `ts/src/evaluator.ts` — add `LetIn` case in evaluate switch
- `spec/test-cases/98-memo-multiarg/` — multi-param memo test
- `spec/test-cases/99-let-in-lambda/` — let bindings in select lambda test
- `docs/syntax-reference.md` — document both features

## 2026-05-12 — Fix `toXml()` XML namespace support (v0.7.10)

`toXml()` in .NET now correctly handles XML namespace declarations and prefixed attributes. Previously, property names containing `:` (like `@xmlns:xsi` or `@xsi:noNamespaceSchemaLocation`) caused `XmlException` because `System.Xml.Linq` requires proper `XNamespace` handling.

Fix: `ValueToXElement` now scans `@xmlns:prefix` attributes first to build a namespace map, then resolves `prefix:localName` in both attributes and element names using the collected namespace URIs. The namespace map is passed down to child elements.

- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — rewrite `ValueToXElement` with namespace resolution; add `ResolveXmlName` helper
- `spec/test-cases/97-toxml-namespaces/` — new test case: `xmlns:xsi` declaration + `xsi:noNamespaceSchemaLocation` prefixed attribute

## 2026-05-08 — Fix `[*]` wildcard to flatten one level (JSONPath semantics) (v0.7.9)

`[*]` on an array-of-arrays now flattens one level, matching standard JSONPath nodeset semantics. Previously, `$.styles[*].colorways[*]` produced nested arrays because auto-map (`.colorways` on an array) creates array-of-arrays and `[*]` was a no-op on arrays. Now `[*]` applies `SelectMany`/`.flat(1)` so chained wildcards like `$.a[*].b[*]` produce a flat list.

Also fixed `JsonNodeValueFactory.ExtractNode` to handle non-`JsonNodeValue` types (like `LazyArrayValue`). Previously, placing a `LazyArrayValue` directly into an object literal serialized it as `null`.

- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — two `[*]` locations: rooted path (line ~96) and `EvaluateIndex` (line ~780) now use `SelectMany` to flatten one level
- `dotnet/src/Elwood.Json/JsonNodeValueFactory.cs` — `ExtractNode` now converts non-JsonNodeValue types via IElwoodValue interface instead of returning null
- `ts/src/evaluator.ts` — `[*]` changed from `toArray(value)` to `toArray(value).flat(1)`
- `spec/test-cases/96-wildcard-flatten/` — new test case: chained `[*]` on nested arrays

## 2026-04-29 — Add trailing `?` optional property marker (`$.prop?`) (v0.7.8)

New syntax: append `?` after a property name to make it optional. `$.default_address?` returns null when the property doesn't exist, instead of throwing. This is the "property may or may not exist" counterpart to `?.` ("target may be null").

Both forms now supported:
- `$.variant?.sku` — returns null if `variant` is null (null-safe navigation)
- `$.variant.sku?` — returns null if `sku` doesn't exist on `variant` (optional property)

- `dotnet/src/Elwood.Core/Syntax/TokenKind.cs` — add `Question` token kind
- `dotnet/src/Elwood.Core/Parsing/Lexer.cs` — tokenize standalone `?`
- `dotnet/src/Elwood.Core/Parsing/Parser.cs` — consume trailing `?` after property names in `ParsePathSegments`
- `ts/src/token.ts` — add `Question` token kind
- `ts/src/lexer.ts` — tokenize standalone `?`
- `ts/src/parser.ts` — consume trailing `?` after property names in `parsePathSegments`
- `spec/test-cases/95-trailing-question-optional/` — new test case

## 2026-04-29 — Fix `?.` optional chaining for missing properties and `$?.` root syntax (v0.7.7)

Two bugs fixed:

**Parser**: `$?.field` failed with "Unexpected character '?'" because the lexer produced `Dollar` + `QuestionDot` tokens and the parser only expected `DollarDot` after `$`. Now the parser checks for `QuestionDot` after consuming a standalone `Dollar` and creates an optional `PropertySegment`.

**Evaluator**: `?.` only returned null when the target was null (e.g. `null?.field`), but still threw "Property not found" when the target was a non-null object missing the property (e.g. `{name: "Alice"}?.address`). Now `?.` returns null in both cases — null target AND missing property on a non-null object. Same fix applied to array auto-map: `array?.missingProp` returns `[]` instead of throwing.

- `dotnet/src/Elwood.Core/Parsing/Parser.cs` — handle `$?.` in `ParsePath()`
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — check `Optional` before throwing in object and array branches of `ResolveProperty`
- `ts/src/parser.ts` — handle `$?.` in `parsePath()`
- `ts/src/evaluator.ts` — check `optional` before throwing in object and array branches of `evalPath`
- `spec/test-cases/94-optional-chaining-missing-prop/` — new test: `$?.missing`, `obj?.missing`, chained `$?.a?.b`

## 2026-04-22 — Support dynamic index expressions in paths (`$.items[variable]`)

The path parser only accepted literal integers, `[*]`, slices, and quoted strings inside brackets. When it encountered a variable or expression (e.g. `$.items[idx]`), it consumed the `[` token but couldn't parse the content, leaving the parser in a broken state and producing `Expected '}'` errors.

Fix: the path parser now backtracks (restores position) when bracket content isn't a literal path construct, letting the postfix parser handle `[expr]` as a dynamic IndexExpression.

- `ts/src/parser.ts` — save/restore `pos` in `parsePathSegments` bracket handling
- `dotnet/src/Elwood.Core/Parsing/Parser.cs` — save/restore `_pos` in `ParsePathSegments` bracket handling
- `spec/test-cases/93-dynamic-index/` — new test: variable, field, nested field, and lambda-variable indices

## 2026-04-22 — Fix `$` scope in explicit lambda parameters

`evalWithLambdaOrImplicit` always rebound `$` to the current pipe element, even when the lambda had explicit parameters (`c => c.field == $.outerField`). This made `$` inside explicit lambdas resolve against the pipe item instead of the outer input, breaking patterns like `| first c => c.colorway == $.style`.

Fix: only rebind `$` when there are no explicit lambda parameters (implicit `$` usage). When a lambda has named parameters, `$` retains its outer scope value.

- `ts/src/evaluator.ts` — `evalWithLambdaOrImplicit`: conditional `$` rebind
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — `EvaluateWithLambdaOrImplicit`: same fix
- `spec/test-cases/92-lambda-dollar-scope/` — new test: explicit lambda `$` vs implicit `$`

## 2026-04-21 — Preserve parse error position info in TS public API

The `evaluate()` and `execute()` catch blocks in `ts/src/index.ts` discarded line/column info from `ParseError` exceptions, returning only the message. Now checks for `ParseError` and uses its `.diagnostic` span info.

- `ts/src/index.ts` — import `ParseError`, use `toDiag(err.diagnostic)` in both catch blocks

## 2026-04-21 — Fix `$root` binding collision with `$` path resolution + bindings API for TS

### Bug fix
Passing a `$root` binding (e.g. to provide a full unsliced document) overwrote the internal scope key that `$` path resolution uses, making `$.field` resolve against the binding instead of the input. Now `$` path resolution uses a separate internal key (`"$"`) that bindings cannot collide with.

After fix:
- `$.field` always resolves from the **input** (first argument to `Execute`/`evaluate`)
- `$root` as an identifier resolves from the **binding** when provided, or defaults to input
- Other bindings (`$source`, `$event`, etc.) work as before

### TS bindings API
`evaluate()` and `execute()` in the TypeScript engine now accept an optional `bindings` parameter, matching the .NET API:
```typescript
execute(script, input, { $root: fullDoc, $source: sourceInfo })
```

### Files
- `dotnet/src/Elwood.Core/ElwoodEngine.cs` — set `"$"` key before bindings loop
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — use `"$"` for path resolution, lambda context, join, memo
- `ts/src/evaluator.ts` — mirror all `$root` → `$` changes, accept bindings in evaluateExpression/evaluateScript
- `ts/src/index.ts` — add optional `bindings` parameter to public `evaluate()` and `execute()`
- `ts/tests/conformance.test.ts` — load `bindings.json` when present
- `dotnet/tests/Elwood.Core.Tests/FileBasedTests.cs` — load `bindings.json` when present
- `spec/test-cases/91-root-binding/` — new test: `$root` binding vs `$` input resolution

## 2026-04-20 — Optional chaining (`?.`) + enriched null-access errors

### Optional chaining
New `?.` operator for safe property access on nullable values, matching the pattern established by C#, Kotlin, Swift, and JavaScript:

```elwood
$.variant?.sku             // returns null if variant is null
$.variant.sku              // throws with enriched error if variant is null
```

Combines naturally with `.omitNulls()` for the common migration pattern:
```elwood
{ name: $.name, sku: $.variant?.sku, size: $.variant?.title }.omitNulls()
```

Works in both path expressions (`$.a?.b`) and member access (`expr?.prop`).

### Enriched null-access errors
When strict access (`.`) hits null, the error now includes:
- Full expression chain: `Expression: $.variant.sku — $.variant is null`
- Pipe iteration context: `While processing item [1] of 3 in | select`
- Fix suggestion: `Did you mean: $.variant?.sku`

### C#/TS consistency
Both engines now throw on strict null path access (previously TS silently returned null). Optional chaining (`?.`) provides the explicit opt-in for safe navigation.

### Files
- `ts/src/token.ts` — `QuestionDot` token
- `ts/src/lexer.ts` — `?.` two-char token recognition
- `ts/src/ast.ts` — `optional` flag on `Property` segment and `MemberAccessExpression`
- `ts/src/parser.ts` — parse `?.` in path segments and postfix
- `ts/src/evaluator.ts` — optional returns null, strict throws enriched error, pipe context tracking
- `dotnet/src/Elwood.Core/Syntax/TokenKind.cs` — `QuestionDot`
- `dotnet/src/Elwood.Core/Parsing/Lexer.cs` — `?.` recognition
- `dotnet/src/Elwood.Core/Syntax/Ast.cs` — `Optional` on `PropertySegment` and `MemberAccessExpression`
- `dotnet/src/Elwood.Core/Parsing/Parser.cs` — parse `?.` in path segments and postfix
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — optional returns null, enriched errors, `BuildPathString`, pipe context
- `docs/syntax-reference.md` — optional chaining documentation
- `docs/changelog.md`

## 2026-04-19 — `.omitNulls()` method

New built-in method that removes null-valued properties from objects. Designed for migration from legacy JSON maps where `nullValueHandling: "Ignore"` automatically strips nulls from output.

```elwood
{ name: $.name, email: $.email, phone: $.phone }.omitNulls()
// If phone is null → { "name": "Alice", "email": "alice@example.com" }
```

- Works on objects (returns new object without null properties) and arrays (maps over elements)
- Shallow: only strips top-level null properties, not nested nulls
- Implemented in both .NET and TypeScript engines

### Files
- `ts/src/evaluator.ts` — `omitNulls` case in callBuiltin
- `dotnet/src/Elwood.Core/Evaluation/Evaluator.cs` — `EvaluateOmitNulls` method
- `spec/test-cases/88-omit-nulls/` (NEW — 4 files)
- `docs/syntax-reference.md` — added to object manipulation table
- `docs/changelog.md`

## 2026-04-18 — Fix CI warnings: bump GitHub Actions to v5, resolve C# nullable/unused warnings

Addressed Node.js 20 deprecation warnings by bumping all GitHub Actions to v5, and fixed C# compiler warnings for nullable dereferences and unused fields.

### Files modified
- `.github/workflows/ci.yml` — bump checkout, setup-dotnet, setup-node to v5
- `.github/workflows/docker.yml` — bump checkout to v5
- `.github/workflows/release.yml` — bump checkout, setup-dotnet, setup-node, upload-artifact, download-artifact to v5
- `.github/workflows/deploy-playground.yml` — bump checkout, setup-node to v5; upload-pages-artifact to v4; deploy-pages to v5
- `dotnet/src/Elwood.Xlsx/XlsxExtension.cs` — fix CS8602 null dereference warnings (Workbook, Worksheet, WorkbookPart)
- `dotnet/tests/Elwood.Core.Tests/FileBasedTests.cs` — remove unused field `_headerWritten`
- `dotnet/tests/Elwood.Parquet.Tests/ParquetTests.cs` — replace `Assert.Equal(true, ...)` with `Assert.True(...)`

---

## 2026-04-19 — Evaluator fix: property access on arrays + playground enhancements

### Bug fix: misleading "Undefined variable" error

When accessing a non-existent property on an array (e.g., `$.users[*]` where the root is an array of objects with no `users` property), the evaluator produced the misleading error "Undefined variable 'u'" instead of identifying the real issue.

**Root cause:** In `evalPath`, the filter `.filter(v => v !== null)` used strict equality, allowing `undefined` (from missing properties) to pass through as array items. These `undefined` values eventually reached lambda binding, where `scope.get('u')` returned `undefined` — indistinguishable from "variable not declared".

**Fix:**
- Changed filter to `.filter(v => v != null)` (loose equality catches both null and undefined)
- When all items in the array lack the requested property, throw a helpful error with suggestions:
  - **Before:** `Undefined variable 'u'.`
  - **After:** `Property 'users' not found on any item in the Array. Available properties: code, label-en_US`

All 143 existing tests pass (86 conformance + 25 unit + 2 benchmark + 28 lexer + 27 parser - some shared across suites).

### Files modified
- `ts/src/evaluator.ts` — property access on arrays: filter fix + helpful error message

---

## 2026-04-19 — Playground: large file mode + size display

Ported three features from the hosted playground to the standalone playground.

### Large File Mode
- Threshold: 1 MB (`LARGE_FILE_THRESHOLD`)
- When input exceeds the threshold, automatically switches the input editor to `plaintext` (disabling syntax highlighting, word wrap, folding, and validation decorations) for performance
- Badge/button in input panel header lets the user toggle: clicking in large file mode shows a confirmation modal warning about potential slowness; clicking in full highlighting mode re-enables large file mode immediately
- State: `largeFileModeOverride` (null = auto-detect, false = user forced full highlighting)

### Input size display
- Formatted file size (B / KB / MB) shown in the input panel header

### Output size display
- Formatted output size shown in the output panel header, next to execution time

### Files
- `playground/src/App.tsx` — large file mode state, threshold, formatSize helper, input/output size in panel headers, editor props for large file mode
- `playground/src/components/LargeFileConfirmModal.tsx` (NEW) — confirmation dialog when disabling large file mode

---

## 2026-04-18 — Playground: server-side share for large files

The share feature previously encoded everything (expression + input + format) into the URL hash using lz-string compression. This produced unusable URLs for large input files (e.g., 50MB JSON).

### Hybrid share approach
- **Small payloads** (compressed URL <= 8000 chars): LZ-string inline in `#data=...` — same as before, no server needed
- **Large payloads** (compressed URL > 8000 chars): uploaded to a Cloudflare Worker + KV, URL becomes `#s=<shortId>`

### Cloudflare Worker (`elwood-share`)
- Deployed at `https://elwood-share.max-favilli.workers.dev`
- `POST /share` — stores `{e, i, f}` in Cloudflare KV, returns `{id}` (8-char random ID)
- `GET /share/:id` — retrieves stored payload
- Max payload: 25 MB, TTL: 90 days, CORS restricted to GitHub Pages origin + localhost

### Files
- `playground/worker/src/index.ts` (NEW) — Cloudflare Worker
- `playground/worker/wrangler.toml` (NEW) — Worker config with KV binding
- `playground/worker/package.json` (NEW)
- `playground/worker/tsconfig.json` (NEW)
- `playground/worker/README.md` (NEW) — setup/deploy instructions
- `playground/src/lib/share-api.ts` (NEW) — `createShare()` and `loadShare()` client helpers
- `playground/src/App.tsx` — hybrid share logic, `#s=` loading on mount, loading overlay
- `playground/src/components/ShareModal.tsx` — loading spinner, error state, expiry note
- `.github/workflows/deploy-playground.yml` — `VITE_SHARE_API` env var in build step

---

## 2026-04-18 — Test case 87: groupBy with memo + bracket property access

New conformance test case combining several features in a real-world product image grouping scenario.

### Features tested
1. Bracket property access — `item["label-en_US"]` for hyphenated property names
2. Memoized function — `memo label => ...` caches colorway extraction
3. String split + interpolation — splits on `_`, recombines via template string
4. groupBy with computed key — groups items by extracted colorway
5. Nested select — `g.items | select i => i.external_url` inside outer select

### Files
- `spec/test-cases/87-groupby-memo-bracket/script.elwood` (NEW)
- `spec/test-cases/87-groupby-memo-bracket/input.json` (NEW)
- `spec/test-cases/87-groupby-memo-bracket/expected.json` (NEW)
- `spec/test-cases/87-groupby-memo-bracket/explanation.md` (NEW)

---

## 2026-04-18 — Documentation: .NET integration guide + editor integration guide

Two new docs to help other coding agents integrate Elwood into projects.

### .NET Integration Guide (`docs/dotnet-integration-guide.md`)
- How to add Elwood.Core + Elwood.Json NuGet packages to a .NET 10 project
- ElwoodEngine API: `Evaluate()` vs `Execute()`, IElwoodValue, variable bindings, custom methods via `RegisterMethod`
- DI registration, error handling, thread safety, complete working example

### Editor Integration Guide (`docs/editor-integration-guide.md`)
- 6-step guide for implementing a Monaco-based Elwood editor in Next.js/React
- Full Monarch tokenizer source, dark theme, React component, context-aware autocomplete provider, real-time error reporting via `@elwood-lang/core` diagnostics

### Files
- `docs/dotnet-integration-guide.md` (NEW)
- `docs/editor-integration-guide.md` (NEW)

## 2026-04-09 — Phase 3 Step 6c: AsyncExecutor

Step-at-a-time executor for `mode: async` pipelines, designed for queue-triggered Functions where each invocation is short-lived.

### What's new
- **`AsyncExecutor`** — `StartAsync` creates state + stores payload/pipeline in IDocumentStore + queues stage 0 sources. `ExecuteStepAsync` processes one source or output per invocation, advances the pipeline when stage/execution completes.
- **`IStepQueue`** interface + `InMemoryStepQueue` for tests. Service Bus impl ships in 6d.
- **`StepMessage`** — ExecutionId, PipelineId, StepType (Source/Output), StepName, StageIndex
- **Fan-in via idempotent steps** — after completing a source, checks all sources in stage. If multiple workers see "all done" and queue the next stage, the duplicate messages are caught by the idempotency check (completed steps are no-ops). Standard at-least-once pattern.
- **8 tests** — start + state, source processing, output completion, multi-stage ordering, concurrent sources queued together, idempotent duplicate handling, failed source halts pipeline, end-to-end drive-the-queue loop

### Design decisions
- **Stateless workers:** pipeline content, trigger payload, IDM, and stage plan are ALL stored in IDocumentStore. Queue workers load everything from storage — no local git clone, no in-memory state between invocations.
- **No atomic counters:** fan-in uses state-based checking + idempotency instead of Redis HINCRBY counters. Simpler, no IStateStore interface changes. At-most-one-extra duplicate message per stage transition.
- **Shared helpers with SyncExecutor:** MergeIntoIdm, EvaluateReference, SerializeValue, DeliverToDestinations are duplicated (not extracted to a shared class) to keep each executor self-contained. Can refactor later if needed.

### Files
- `dotnet/src/Elwood.Pipeline/Async/AsyncExecutor.cs` (NEW)
- `dotnet/src/Elwood.Pipeline/Async/IStepQueue.cs` (NEW)
- `dotnet/src/Elwood.Pipeline/Async/InMemoryStepQueue.cs` (NEW)
- `dotnet/tests/Elwood.Pipeline.Tests/AsyncExecutorTests.cs` (NEW — 8 tests)
- `docs/roadmap.md`, `docs/changelog.md`

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
