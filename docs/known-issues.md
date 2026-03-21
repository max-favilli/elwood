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

## .NET implementation

No known issues — all 97 tests passing.
