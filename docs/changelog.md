# Changelog

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
