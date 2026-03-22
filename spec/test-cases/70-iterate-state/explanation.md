# 70 — Iterate with State

## Expression
```
iterate({ n: 1, total: 0 }, s => { n: s.n + 1, total: s.total + s.n }) | take 4 | last
```

## Explanation
`iterate` can carry complex state across iterations using objects. Each step produces a new object based on the previous one:

```
Step 0: { n: 1, total: 0 }            (seed)
Step 1: { n: 2, total: 0 + 1 = 1 }
Step 2: { n: 3, total: 1 + 2 = 3 }
Step 3: { n: 4, total: 3 + 3 = 6 }
```

`| take 4` limits to 4 iterations, `| last` returns only the final value.

This pattern is useful for accumulating results across iterations — like a running total, building up a complex result, or simulating stateful processes functionally.
