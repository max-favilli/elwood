# 49 — Truncate

## Expression
```
$.Qty.toNumber().truncate()
```

## Traditional JSONPath equivalent
```
$.Qty.Truncate()
```

## Explanation
- `$.Qty` → `"5.6"` (string)
- `.toNumber()` → `5.6`
- `.truncate()` → `5` (remove decimal part, no rounding)

### truncate vs floor vs round
| Method | 5.6 → | -5.6 → | Behavior |
|---|---|---|---|
| `.truncate()` | 5 | -5 | Remove decimals (toward zero) |
| `.floor()` | 5 | -6 | Round down (toward negative infinity) |
| `.ceiling()` | 6 | -5 | Round up (toward positive infinity) |
| `.round()` | 6 | -6 | Round to nearest (away from zero at midpoint) |

The difference between `truncate` and `floor` only matters for negative numbers: `truncate(-5.6)` → `-5`, `floor(-5.6)` → `-6`.
