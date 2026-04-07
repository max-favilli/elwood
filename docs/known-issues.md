# Known Issues

## TypeScript implementation

### Crypto functions — browser limitation
- **Status:** Working in Node.js, not available in browsers
- **Details:** `hash()` and `rsaSign()` use `node:crypto` (synchronous). In browsers, these functions will throw an error explaining the limitation.
- **Future fix:** Use Web Crypto API (`crypto.subtle`) with async wrapper, or a WASM-based sync crypto library.

### Lazy evaluation not yet implemented
- **Status:** TS evaluator uses eager arrays (`.filter()`, `.map()`) instead of generators
- **Impact:** Higher memory usage on large datasets compared to .NET engine
- **Details:** The .NET engine uses `LazyArrayValue` with `IEnumerable`/`yield return`. The TS port should use `function*`/`yield` generators for the same benefit.
- **Plan:** Implement in Phase 4 alongside compiled mode optimizations.

### `@elwood-lang/parquet` not published to npm
- **Status:** `Elwood.Parquet` is published to nuget.org for .NET, but `@elwood-lang/parquet` is **not** published to npm.
- **Details:** The only mature JS Parquet reader (`hyparquet`) has an async-only `parquetRead` API (`Promise<void>`, data via `onComplete` callback). Elwood's TS extension API in `ts/src/extensions.ts` is synchronous — extension method handlers must return their result directly. The two are incompatible: by the time `parquetRead` resolves, the Elwood evaluator has already moved on.
- **Workaround:** Use `Elwood.Parquet` (.NET) for Parquet I/O — it backs onto `Parquet.Net` which has a synchronous API.
- **Future fix:** Two options — (1) make Elwood's TS extension API async (touches the entire evaluator + every extension contract), or (2) find/build a synchronous Parquet reader for JS (e.g. WASM-compiled). Tracked as a follow-up.

## .NET implementation

No known issues — all 97 tests passing.
