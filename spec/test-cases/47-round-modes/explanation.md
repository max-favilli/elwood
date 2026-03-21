# 47 — Round with Modes

## Expression
```
{
  awayFromZero: $.Qty.toNumber().round("awayFromZero"),
  toEven: $.Qty.toNumber().round("toEven"),
  default: $.Qty.toNumber().round()
}
```

## Traditional JSONPath equivalent
```
$.Qty.Round(AwayFromZero)   → 3
$.Qty.Round(ToEven)         → 2
```

## Explanation
When a number is exactly at the midpoint (e.g. `2.5`), the rounding mode determines which way it goes:

| Mode | 2.5 → | Behavior |
|---|---|---|
| `"awayFromZero"` | **3** | Always round away from zero at midpoint (standard rounding) |
| `"toEven"` | **2** | Round to nearest even number at midpoint (banker's rounding) |

### round() forms
```
.round()                      → round to integer, awayFromZero (default)
.round(2)                     → round to 2 decimal places
.round("toEven")              → round to integer, banker's rounding
.round(2, "toEven")           → round to 2 decimals, banker's rounding
```

### Why it matters
- **AwayFromZero** (default) — intuitive, matches what most people expect: 2.5 → 3
- **ToEven** (banker's rounding) — reduces systematic bias in financial calculations: 2.5 → 2, 3.5 → 4. Used in accounting and SAP.

### Default behavior
Elwood defaults to `AwayFromZero` to match traditional behavior. Note that .NET's `Math.Round()` defaults to `ToEven` — Elwood overrides this to avoid surprising users migrating from traditional JSONPath.
