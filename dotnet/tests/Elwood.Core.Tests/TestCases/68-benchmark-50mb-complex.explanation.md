# 68 — Benchmark: 50MB Complex Pipeline

> **This is a benchmark test case — no input/expected files.**
> It runs as a code-based benchmark in `Benchmarks/LargeDocumentBenchmark.cs`.
> Results are logged to `Benchmarks/results.log`.

## Script
```
let categoryTier = memo catId =>
    $.categories[*] | first c => c.id == catId

$.orders[*]
| where o => o.status == "confirmed" || o.status == "shipped"
| select o => {
    orderId: o.id,
    customer: o.customerId,
    date: o.date,
    priorityLabel: if o.priority == "high" then "URGENT"
                   else if o.priority == "medium" then "NORMAL"
                   else "LOW",
    premiumItemCount: o.items[*]
        | where i => categoryTier(i.categoryId).tier == "premium"
        | count
}
```

## What it tests
A realistic, complex transformation on a ~73MB JSON document:

- **200,000 orders** with 2-5 line items each
- **50 categories** as a lookup table
- **`memo`**: memoized category lookup (50 unique keys → 50 cache entries)
- **`where`**: filter to 40% of orders (confirmed + shipped)
- **`select`**: build output objects with 5 properties
- **Nested pipeline**: for each order, filter its items array against the memo'd category tier
- **`if/then/else`**: conditional priority label mapping

## Benchmark results (typical)
| Metric | Value |
|---|---|
| Input | 200K orders, ~73MB JSON |
| Output | 80,000 rows |
| Time | **~1,925ms** |
| Throughput | **~41,500 rows/sec** |
| Memory delta | **~106MB** |

## Techniques demonstrated
1. **`memo`** — category lookup computed once per unique category ID (50 total), not once per order (200K)
2. **Lazy `where`** — streams orders without materializing the full filtered array
3. **Nested `where | count`** — for each order, filters its items and counts matches
4. **`if/then/else`** chain — maps priority codes to labels
5. **`let` binding** — defines the memoized function once, used in every `select` iteration
