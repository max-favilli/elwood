# Changelog

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
