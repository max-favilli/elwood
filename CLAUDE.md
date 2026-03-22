# Elwood — JSON Transformation DSL

## Project Structure
```
Elwood/
├── docs/                              # Shared documentation
├── samples/                           # Example scripts and sample data
├── spec/                              # Shared conformance test suite
│   └── test-cases/
│       ├── 01-property-access/
│       │   ├── script.elwood
│       │   ├── input.json
│       │   ├── expected.json
│       │   └── explanation.md
│       └── ...                        # 68 test cases
├── dotnet/                            # .NET implementation
│   ├── src/
│   │   ├── Elwood.Core/              # Parser, AST, Evaluator, built-in functions
│   │   ├── Elwood.Json/              # System.Text.Json adapter (JsonNode)
│   │   ├── Elwood.Newtonsoft/        # Placeholder for Newtonsoft.Json adapter
│   │   └── Elwood.Cli/              # CLI tool (REPL, eval, run)
│   ├── tests/
│   │   └── Elwood.Core.Tests/       # Code-based tests + benchmarks
│   │       └── Benchmarks/          # Performance benchmarks + timing.log
│   └── Elwood.slnx
├── ts/                                # TypeScript implementation (Phase 1b)
├── .private/                          # Private planning docs (gitignored)
└── CLAUDE.md
```

## Important constraints
- **Never use the word "Eagle" in any public file** (source, docs, tests, comments). Use generic terms like "traditional JSONPath" or "legacy approach". See `.private/eagle-migration.md` for context.
- **Elwood is a functional language — keep it that way.** No imperative features: no `for`/`while` loops, no mutable variables, no variable reassignment, no statements. Everything is an expression. Data transformation is expressed through pipe operators (`where`, `select`, `reduce`, etc.) and lambda expressions, not through imperative control flow. If a user requests a feature that would require imperative constructs, challenge it and propose a functional alternative. See `docs/functional-programming.md` for the design philosophy.

## Working with Claude

### Always update docs when changing the language
When adding new syntax, pipe operators, built-in methods, or fixing bugs:
1. **Update `docs/syntax-reference.md`** — add the new feature to the appropriate section
2. **Update `docs/changelog.md`** — append a dated entry with description and files modified
3. **Add a spec test case** in `spec/test-cases/{name}/` with up to four files:
   - `script.elwood` — the expression or script
   - `input.json` — the input JSON
   - `expected.json` — the expected output
   - `explanation.md` — explanation with traditional JSONPath equivalent (if applicable)
4. Benchmark-only test cases omit `input.json` and `expected.json` (documentation stubs)

### Always ask before committing or creating PRs
Never run `git commit`, `git push`, or create a pull request without first asking the user to review the changes locally.

### Challenge requests that seem architecturally incorrect
If a request doesn't sound architecturally right — for example, adding a function that duplicates existing functionality, or breaking the pipe/method duality — stop and challenge it before implementing.

### Test naming convention
Spec test cases are numbered sequentially: `01-property-access`, `02-where-filter`, etc. Each test should focus on one concept. The same test cases are shared between .NET and TypeScript implementations.

### Non-deterministic functions
Functions like `now()`, `utcNow()`, `newGuid()` cannot be tested with spec test cases. Use code-based tests in `dotnet/tests/Elwood.Core.Tests/EndToEndTests.cs` (and `ts/tests/unit/non-deterministic.test.ts` for TS) that verify properties rather than exact values.

### Building and testing
```bash
# .NET
dotnet build dotnet/Elwood.slnx
dotnet test dotnet/tests/Elwood.Core.Tests/

# TypeScript (when available)
cd ts && npm test
```
