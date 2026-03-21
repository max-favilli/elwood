# Elwood TypeScript Port — Implementation Plan

## Goal

Create a TypeScript implementation of the Elwood DSL engine that runs in browsers, Node.js, and edge runtimes. Both the .NET and TypeScript implementations share a conformance test suite to guarantee identical behavior.

---

## Step 0: Lazy Evaluation in .NET Engine (Prerequisite)

Before restructuring or starting the TS port, refactor the .NET evaluator to use lazy/streaming pipes. This ensures the TS implementation follows the correct model from day one.

### Why
Elwood must handle JSON documents up to 100MB (arrays with hundreds of thousands of elements). The current evaluator materializes intermediate arrays at each pipe stage. For `where | select | take(10)` on 200K elements, that's 3 full array copies in memory. Lazy evaluation streams elements one at a time, materializing only when necessary.

### Steps

1. Refactor pipe operators in `Evaluator.cs` to return `IEnumerable<IElwoodValue>` with `yield return` instead of `List<IElwoodValue>`:
   - **Lazy (streaming):** `where`, `select`, `selectMany`, `take`, `skip`, `distinct`, `concat`, `index`
   - **Materializing (need all data):** `orderBy`, `groupBy`, `count`, `sum`, `min`, `max`, `last`, `reverse`, `batch`, `join`, `reduce`
2. Ensure `take(n)` short-circuits — stops pulling from upstream after n elements
3. Add a benchmark test case: large JSON array (1MB+ in git) with multi-stage pipeline, measuring time and peak memory
4. Verify all 65+ existing conformance tests still pass
5. Run benchmark before/after to confirm improvement

### Impact on TS port
The TypeScript evaluator will use **generators** (`function*` / `yield`) to implement the same lazy model. This decision should be baked in from the start, not retrofitted.

---

## Step 1: Restructure the Repository

Reorganize the monorepo to support multiple implementations sharing a single spec.

### Current structure
```
Elwood/
├── docs/
├── samples/
├── src/
│   ├── Elwood.Core/
│   ├── Elwood.Json/
│   ├── Elwood.Newtonsoft/
│   └── Elwood.Cli/
├── tests/
│   └── Elwood.Core.Tests/
│       └── TestCases/          ← 65 test triplets live here
└── Elwood.slnx
```

### Pre-restructure cleanup ✅
All 48 explanation files in `tests/Elwood.Core.Tests/TestCases/` have been cleaned — references to specific legacy systems replaced with generic terms ("traditional JSONPath", etc.). Sample data and examples also updated.

### Target structure
```
Elwood/
├── docs/                           # shared documentation
├── samples/                        # shared sample scripts
│
├── spec/                           # ← NEW: shared conformance suite
│   └── test-cases/
│       ├── 01-property-access/
│       │   ├── script.elwood
│       │   ├── input.json
│       │   ├── expected.json
│       │   └── explanation.md
│       ├── 02-where-filter/
│       │   └── ...
│       └── ...                     # all 65+ test cases
│
├── dotnet/                         # ← moved from root
│   ├── src/
│   │   ├── Elwood.Core/
│   │   ├── Elwood.Json/
│   │   ├── Elwood.Newtonsoft/
│   │   └── Elwood.Cli/
│   ├── tests/
│   │   └── Elwood.Core.Tests/     # code-based tests only (non-deterministic, etc.)
│   └── Elwood.slnx
│
├── ts/                             # ← NEW: TypeScript implementation
│   ├── src/
│   │   ├── lexer.ts
│   │   ├── parser.ts
│   │   ├── ast.ts
│   │   ├── evaluator.ts
│   │   ├── functions/
│   │   │   ├── string.ts
│   │   │   ├── numeric.ts
│   │   │   ├── datetime.ts
│   │   │   ├── array.ts
│   │   │   ├── crypto.ts
│   │   │   ├── null-checks.ts
│   │   │   └── index.ts
│   │   └── index.ts               # public API
│   ├── tests/
│   │   ├── conformance.test.ts    # reads from ../../spec/test-cases/
│   │   └── unit/                  # TS-specific unit tests
│   ├── package.json
│   ├── tsconfig.json
│   └── vitest.config.ts
│
└── CLAUDE.md                       # updated with new paths
```

### Detailed steps

1. Create `spec/test-cases/` directory
2. Move each test case from `tests/Elwood.Core.Tests/TestCases/` into its own subdirectory under `spec/test-cases/`:
   - `01-property-access.elwood` → `spec/test-cases/01-property-access/script.elwood`
   - `01-property-access.input.json` → `spec/test-cases/01-property-access/input.json`
   - `01-property-access.expected.json` → `spec/test-cases/01-property-access/expected.json`
   - `01-property-access.explanation.md` → `spec/test-cases/01-property-access/explanation.md`
3. Move `src/` → `dotnet/src/`, `tests/` → `dotnet/tests/`, `Elwood.slnx` → `dotnet/Elwood.slnx`
4. Update the .NET solution file and all `<ProjectReference>` paths to reflect the new locations
5. Update the .NET conformance test runner (`FileBasedTests.cs` or similar) to read from `../../spec/test-cases/` instead of the local `TestCases/` directory
6. Verify all .NET tests still pass after the move: `dotnet test dotnet/tests/Elwood.Core.Tests/`
7. Update `CLAUDE.md` with the new project structure

**Important:** Do NOT create the `ts/` directory yet. Complete the restructure first and verify everything works before starting the TypeScript implementation.

---

## Step 2: Initialize the TypeScript Project

Create the TypeScript project skeleton inside `ts/`.

### Steps

1. Create `ts/` directory
2. Initialize with `npm init` — package name: `@elwood-lang/core` (or `elwood-lang`)
3. Install dev dependencies: `typescript`, `vitest`, `@types/node`
4. Create `tsconfig.json`:
   - Target: `ES2022`
   - Module: `ESNext` with `moduleResolution: bundler`
   - Strict mode enabled
   - Output to `dist/`
   - Dual CJS/ESM output (for Node.js + bundler compatibility)
5. Create `vitest.config.ts`
6. Create the conformance test runner (`tests/conformance.test.ts`):
   - Scan `../../spec/test-cases/*/`
   - For each directory: read `script.elwood`, `input.json`, `expected.json`
   - Call `evaluate(script, input)` and deep-compare result to expected
   - Each test case should be a separate test (use `describe.each` or similar)
7. Create a stub `src/index.ts` that exports an `evaluate(script: string, input: unknown): unknown` function (initially throws "not implemented")
8. Verify the test harness runs and all 65 tests fail with "not implemented" (this confirms the harness works)

### Public API design

```typescript
// src/index.ts
export function evaluate(script: string, input: unknown): unknown;
export function parse(script: string): AstNode;

// Types
export interface EvaluateOptions {
  // future: custom functions, timeout, etc.
}
```

---

## Step 3: Port the Lexer

Port `Elwood.Core/Lexer.cs` (and `Token.cs`, `TokenType.cs`) to TypeScript.

### What to port
- All token types (identifiers, numbers, strings, operators, pipes, etc.)
- String interpolation tokenization (backtick strings with `{expr}` segments)
- Single-line comments (`//`)
- All operators: `==`, `!=`, `>=`, `<=`, `>`, `<`, `&&`, `||`, `!`, `+`, `-`, `*`, `/`, `%`
- Special tokens: `|`, `=>`, `..`, `[`, `]`, `{`, `}`, `(`, `)`, `:`, `,`, `...` (spread)
- Keywords: `let`, `memo`, `if`, `then`, `else`, `true`, `false`, `null`, `in`, `not`, `and`, `or`, `return`
- Position tracking (line, column) for error messages

### Reference files
- `dotnet/src/Elwood.Core/Lexer.cs`
- `dotnet/src/Elwood.Core/Token.cs`
- `dotnet/src/Elwood.Core/TokenType.cs`

### Output
- `ts/src/lexer.ts` — `tokenize(source: string): Token[]`
- `ts/src/token.ts` — `Token` type and `TokenType` enum

### Verification
- Write unit tests in `ts/tests/unit/lexer.test.ts` that tokenize sample expressions and verify token sequences

---

## Step 4: Port the Parser

Port `Elwood.Core/Parser.cs` to TypeScript. This is the largest single piece of work.

### What to port
- Recursive descent parser producing an AST
- All expression types: binary, unary, member access, index, slice, function call, pipe, lambda, let binding, memo, if/then/else, match, object literal, array literal, spread, string interpolation, recursive descent (`$..field`)
- Operator precedence (same as C#: `||` < `&&` < `==`/`!=` < `<`/`>`/`<=`/`>=` < `+`/`-` < `*`/`/`/`%` < unary)
- Error messages with position info and "Did you mean?" suggestions

### Reference files
- `dotnet/src/Elwood.Core/Parser.cs`
- `dotnet/src/Elwood.Core/Ast/` — all AST node classes

### Output
- `ts/src/parser.ts` — `parse(tokens: Token[]): AstNode`
- `ts/src/ast.ts` — AST node type definitions (use discriminated unions, not classes)

### TypeScript AST design guidance
Use discriminated unions instead of class hierarchies:
```typescript
type AstNode =
  | { type: 'BinaryExpression'; op: string; left: AstNode; right: AstNode }
  | { type: 'MemberAccess'; object: AstNode; property: string }
  | { type: 'PipeExpression'; left: AstNode; right: AstNode }
  | { type: 'Lambda'; params: string[]; body: AstNode }
  // ... etc
```

### Verification
- Write unit tests that parse expressions and verify AST structure
- At this point, some conformance tests may start being partially useful (parse + evaluate simple expressions)

---

## Step 5: Port the Evaluator

Port `Elwood.Core/Evaluator.cs` to TypeScript.

### What to port
- Tree-walk evaluator that takes an AST node and input value, returns a result
- Scope management (variable bindings from `let`, lambda params, pipe context)
- Implicit `$` context in pipe operations
- Auto-mapping: when you access a property on an array, it maps over the array
- JSONPath navigation: `$`, `$.field`, `$[0]`, `$[*]`, `$[2:5]`, `$[-2:]`, `$..field`
- **Lazy pipe evaluation using generators** (mirrors the .NET `IEnumerable`/`yield return` model from Step 0)
- Object/array literal construction
- Spread operator in objects
- String interpolation evaluation

### Lazy pipes — critical for performance
Pipe operators must use JavaScript generators (`function*` / `yield`) to stream elements lazily, matching the .NET implementation from Step 0:

```typescript
// Lazy — streams elements one at a time
function* whereOp(input: Iterable<unknown>, predicate: (item: unknown) => boolean) {
    for (const item of input) {
        if (predicate(item)) yield item;
    }
}

// take(n) short-circuits — stops pulling after n elements
function* takeOp(input: Iterable<unknown>, n: number) {
    let count = 0;
    for (const item of input) {
        if (count >= n) return;
        yield item;
        count++;
    }
}
```

**Lazy (streaming):** `where`, `select`, `selectMany`, `take`, `skip`, `distinct`, `concat`, `index`
**Materializing (need all data):** `orderBy`, `groupBy`, `count`, `sum`, `min`, `max`, `last`, `reverse`, `batch`, `join`, `reduce`

The final result is materialized to a plain array/value only at the end of evaluation (or when a materializing operator is encountered).

### Key difference from .NET
In .NET, the evaluator works with `IElwoodValue` (an abstraction over JSON). In TypeScript, we work directly with plain JavaScript values (`unknown`). This is actually simpler — no abstraction layer needed. JSON parses directly to JS objects/arrays/primitives.

### Reference files
- `dotnet/src/Elwood.Core/Evaluator.cs`
- `dotnet/src/Elwood.Core/Scope.cs` (or equivalent)

### Output
- `ts/src/evaluator.ts` — `evaluate(node: AstNode, input: unknown, scope: Scope): unknown`

### Verification
- At this point, basic conformance tests should start passing (property access, arithmetic, if/then/else, let bindings)

---

## Step 6: Port Built-in Functions

Port all 70+ built-in methods. Do these in batches, running conformance tests after each batch.

### Batch order (by conformance test coverage)
1. **Array/pipe operators** — `where`, `select`, `selectMany`, `orderBy`, `groupBy`, `distinct`, `take`, `skip`, `batch`, `count`, `sum`, `min`, `max`, `first`, `last`, `any`, `all`, `concat`, `index`, `reduce`, `join`, `match`
2. **String methods** — `toLower`, `toUpper`, `trim`, `trimStart`, `trimEnd`, `startsWith`, `endsWith`, `contains`, `replace`, `split`, `substring`, `left`, `right`, `padLeft`, `padRight`, `indexOf`, `length`, `urlEncode`, `urlDecode`, `base64Encode`, `base64Decode`, `sanitize`, `graphqlString`
3. **Numeric methods** — `round`, `floor`, `ceiling`, `abs`, `truncate`, `convertTo`
4. **DateTime methods** — `now`, `utcNow`, `dateFormat`, `tryDateFormat`, `dateAdd`, `dateDiff`, `unixTimestamp`, `fromUnixTimestamp`
5. **Crypto methods** — `hash`, `hmac`, `rsaSign`
6. **Null-check methods** — `isNull`, `isNotNull`, `coalesce`, `ifNull`
7. **Object methods** — `clone`, `keep`, `remove`, `keys`, `values`, `hasProperty`
8. **Type methods** — `typeOf`, `isArray`, `isObject`, `isString`, `isNumber`, `isBool`

### Reference files
- `dotnet/src/Elwood.Core/Functions/` — all built-in function implementations
- `dotnet/src/Elwood.Core/BuiltInFunctions.cs` (or similar registration file)

### Output
- `ts/src/functions/*.ts` — one file per category
- `ts/src/functions/index.ts` — registry that maps function names to implementations

### Verification
- Run conformance tests after each batch. Target: all 65 conformance tests passing by end of this step.

### Notes on crypto in the browser
- `hash`, `hmac`, `rsaSign` — use the Web Crypto API (`crypto.subtle`) in browsers and `node:crypto` in Node.js
- These will need to be **synchronous** in our API (the .NET version is sync). Options:
  - Use synchronous `node:crypto` in Node.js, and for browsers either: (a) make `evaluate` async, or (b) use a WASM-based sync crypto library
  - Decision: start with Node.js `crypto` module, add browser support later as a follow-up. Document the limitation.

---

## Step 7: Error Reporting

Port the error reporting system.

### What to port
- Error messages with line/column positions
- "Did you mean?" suggestions for misspelled function names and properties
- Clear error messages for common mistakes (missing pipe operator, wrong argument count, etc.)

### Reference files
- Error handling code scattered across `Lexer.cs`, `Parser.cs`, `Evaluator.cs`
- `dotnet/src/Elwood.Core/ElwoodException.cs` (or similar)

---

## Step 8: Package and Publish

**Note:** Packaging and publishing is covered by Phase 1c in the roadmap. This step is about making the TS project publish-ready; actual publishing happens as part of the coordinated Phase 1c launch (GitHub, NuGet, npm, CI together).

### npm package setup
- Package name: `@elwood-lang/core` (or `elwood-lang` if no org)
- Dual ESM/CJS output
- TypeScript declarations included
- Zero runtime dependencies
- `exports` field in `package.json` for proper module resolution
- `browser` field to handle crypto shimming

### Files to include in the package
```
dist/
  index.js          # ESM
  index.cjs         # CJS
  index.d.ts        # TypeScript declarations
```

### README for the npm package
- Quick start with `evaluate()` example
- Link to syntax reference
- Browser vs Node.js notes (crypto limitations)

---

## Keeping Implementations in Sync — Ongoing Process

### When adding a new feature

1. Write the spec first: add a new test case in `spec/test-cases/` with all 4 files
2. Implement in .NET — verify `dotnet test` passes
3. Implement in TypeScript — verify `npm test` passes in `ts/`
4. Update `docs/syntax-reference.md` and `docs/changelog.md`

### CI pipeline (future)

```yaml
# Runs on every PR
jobs:
  dotnet:
    - dotnet test dotnet/tests/Elwood.Core.Tests/
  typescript:
    - cd ts && npm ci && npm test
  conformance-check:
    - verify both implementations pass all spec/test-cases/
```

### Spec test case format

Each test case is a directory under `spec/test-cases/` containing:

| File | Purpose |
|------|---------|
| `script.elwood` | The Elwood expression to evaluate |
| `input.json` | The input JSON document |
| `expected.json` | The expected output (deep equality check) |
| `explanation.md` | Human explanation + traditional JSONPath comparison |

The conformance runner on each side reads these files and verifies: `evaluate(script, input) == expected`.

### Non-deterministic tests

Functions like `now()`, `utcNow()`, `newGuid()` cannot be tested with file-based conformance tests. These need code-based tests on each side that verify properties (format, type) rather than exact values. Keep these in:
- `dotnet/tests/Elwood.Core.Tests/EndToEndTests.cs`
- `ts/tests/unit/non-deterministic.test.ts`

---

## Implementation Order Summary

| Step | What | Depends on | Estimated conformance tests passing |
|------|------|-----------|--------------------------------------|
| 0 | Lazy evaluation in .NET | — | All .NET tests still pass + benchmark |
| 1 | Restructure repo | Step 0 | All .NET tests still pass |
| 2 | Init TS project + test harness | Step 1 | 0/65 (all fail with "not implemented") |
| 3 | Port lexer | Step 2 | 0/65 |
| 4 | Port parser | Step 3 | 0/65 |
| 5 | Port evaluator (core + lazy pipes) | Step 4 | ~10/65 (basic expressions) |
| 6 | Port built-in functions | Step 5 | 65/65 |
| 7 | Error reporting | Step 5 | 65/65 (improves error quality) |
| 8 | Package-ready (publish in Phase 1c) | Step 6 | 65/65 |

---

## References

These .NET source files are the reference implementation. The TypeScript port should produce identical behavior for all inputs:

- **Lexer:** `dotnet/src/Elwood.Core/Lexer.cs`, `Token.cs`, `TokenType.cs`
- **Parser:** `dotnet/src/Elwood.Core/Parser.cs`
- **AST:** `dotnet/src/Elwood.Core/Ast/` (all files)
- **Evaluator:** `dotnet/src/Elwood.Core/Evaluator.cs`
- **Built-in functions:** `dotnet/src/Elwood.Core/Functions/` (all files)
- **Syntax reference:** `docs/syntax-reference.md`
- **Conformance tests:** `spec/test-cases/` (after restructure)
