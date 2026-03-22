# 72 — Iterate: Fibonacci Sequence

## Expression
```
iterate({ a: 0, b: 1 }, s => { a: s.b, b: s.a + s.b }) | take 8 | select s => s.a
```

## Explanation
The Fibonacci sequence expressed functionally with `iterate`:

- **Seed:** `{ a: 0, b: 1 }` — the first two Fibonacci numbers
- **Step function:** `s => { a: s.b, b: s.a + s.b }` — shift forward: `a` becomes `b`, `b` becomes `a + b`
- **`| take 8`** — first 8 terms
- **`| select s => s.a`** — extract just the `a` value from each state object

### Step-by-step
```
Step 0: { a: 0, b: 1 }    → 0
Step 1: { a: 1, b: 1 }    → 1
Step 2: { a: 1, b: 2 }    → 1
Step 3: { a: 2, b: 3 }    → 2
Step 4: { a: 3, b: 5 }    → 3
Step 5: { a: 5, b: 8 }    → 5
Step 6: { a: 8, b: 13 }   → 8
Step 7: { a: 13, b: 21 }  → 13
```

This demonstrates that any iterative algorithm can be expressed functionally with `iterate` — no mutable variables, no loops.
