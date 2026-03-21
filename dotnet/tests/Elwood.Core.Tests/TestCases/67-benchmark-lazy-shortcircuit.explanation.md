# 67 — Benchmark: Lazy Short-Circuit

> **This is a benchmark test case — no input/expected files.**
> It runs as a code-based benchmark in `Benchmarks/LazyEvaluationBenchmark.cs`.
> Results are logged to `Benchmarks/results.log`.

## Expression
```
$.items[*] | where i => i.active | select i => i.name | take 10
```

## What it tests
A pipeline of streaming operators on a 100K-element array, ending with `take 10`.

With lazy evaluation, `take(10)` short-circuits — the upstream `where` and `select` only process ~15 items (enough to find 10 that pass the filter). Without lazy eval, all 100K items would be materialized at each stage.

## Benchmark results (typical)
| Metric | Value |
|---|---|
| Input | 100,000 items |
| Output | 10 items |
| `where\|select\|take(10)` | **~0.02ms** avg |
| `take(1)` alone | **~19ms** (includes parse overhead) |
| Memory delta | **~24KB** |

## Why it matters
For pipelines that filter a large dataset down to a small result, lazy evaluation avoids allocating intermediate arrays. On a 100K array:
- **Eager**: 3 array copies (~300K allocations) before `take` discards 99.99%
- **Lazy**: streams ~15 items, stops. O(1) memory per stage.
