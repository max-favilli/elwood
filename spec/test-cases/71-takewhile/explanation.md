# 71 — TakeWhile

## Expression
```
iterate(1, x => x + 1) | takeWhile x => x < 6
```

## Explanation
`takeWhile` takes items from a sequence while a predicate is true, then stops immediately:

- `iterate(1, x => x + 1)` generates an infinite sequence: 1, 2, 3, 4, 5, 6, 7, ...
- `| takeWhile x => x < 6` takes values while they're less than 6, then stops

Result: `[1, 2, 3, 4, 5]`

### Why takeWhile matters
`takeWhile` **short-circuits** — it stops pulling from upstream as soon as the predicate returns false. This is critical for infinite sequences like `iterate`, where `where` would try to check every element (infinite loop) but `takeWhile` knows to stop.

### takeWhile vs where
| Operator | Behavior | Safe with infinite sequences? |
|---|---|---|
| `where` | Checks every element, keeps matches | No — never terminates |
| `takeWhile` | Stops at first non-match | Yes — short-circuits |
| `take n` | Stops after n elements | Yes — short-circuits |
