# 69 — Iterate (Basic)

## Expression
```
iterate(1, x => x * 2) | take 5
```

## Explanation
`iterate(seed, fn)` generates a lazy sequence where each value is produced by applying the function to the previous value:

```
iterate(1, x => x * 2)  →  [1, 2, 4, 8, 16, 32, 64, ...]  (infinite)
```

The sequence is **infinite** — it must be limited by a downstream operator like `take`, `takeWhile`, or `first`.

- `1` — the seed (first value)
- `x => x * 2` — each subsequent value is double the previous
- `| take 5` — stop after 5 values

This is a functional alternative to imperative loops. Instead of mutating a variable in a for-loop, you describe the transformation as a function applied repeatedly.

### How it works internally
```
Step 0: yield 1          (seed)
Step 1: yield 1 * 2 = 2
Step 2: yield 2 * 2 = 4
Step 3: yield 4 * 2 = 8
Step 4: yield 8 * 2 = 16
(take 5 stops here)
```
