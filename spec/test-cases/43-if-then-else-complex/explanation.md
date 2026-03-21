# 43 — Complex If/Then/Else with Null Check, Filtering, and Aggregation

## Script
```
let deadline = $.E1EDP37.deadline

if $.E1EDP20.isNullOrEmpty() then 0
else $.E1EDP20[*]
  | where e => e.EDATU < deadline
  | select e => e.WMENG.toNumber()
  | sum
```

## Traditional JSONPath equivalent
```
$.if($.E1EDP20.isNullOrEmpty()).then('0').else(
  $.E1EDP20[*].where($.EDATU < $$$.E1EDP37.deadline).WMENG.convertTo('Int32').sum()
)
```

## Explanation
This is a real-world SAP IDoc processing pattern:

1. **Null safety**: `$.E1EDP20.isNullOrEmpty()` — check if the schedule lines array exists
2. **If empty** → return `0` (no quantity)
3. **If not empty** → filter, convert, and sum:
   - `| where e => e.EDATU < deadline` — keep only schedule lines before the deadline
   - `| select e => e.WMENG.toNumber()` — extract the quantity and convert from string `"1.000"` to number `1`
   - `| sum` — sum all quantities

### Step by step with test data
- `deadline` = `"20260324"`
- Schedule lines:
  - `"20260121"` < `"20260324"` → ✓ (WMENG: 1)
  - `"20260122"` < `"20260324"` → ✓ (WMENG: 1)
  - `"20260522"` < `"20260324"` → ✗ (after deadline)
- Sum: 1 + 1 = **2**

### traditional JSONPath vs Elwood comparison

| Concept | traditional JSONPath | Elwood |
|---|---|---|
| If/then/else | `.if(cond).then(val).else(val)` (chained methods) | `if cond then val else val` (keyword expression) |
| Null check | `.isNullOrEmpty()` | `.isNullOrEmpty()` (same) |
| Root access in filter | `$$$.E1EDP37.deadline` (count parent hops) | `let deadline = ...` (named variable) |
| Type conversion | `.convertTo('Int32')` | `.toNumber()` (or `.convertTo("Int32")`) |
| Auto-mapping | `.WMENG` (implicit) | `\| select e => e.WMENG` (explicit) |

### Null/empty check methods
```
$.value.isNull()              // true if null
$.value.isEmpty()             // true if null, empty string, or empty array
$.value.isNullOrEmpty()       // same as isEmpty()
$.value.isNullOrWhiteSpace()  // true if null, empty, or whitespace-only string
```

### if/then without else
traditional JSONPath supports `.if().then()` without `.else()`. In Elwood, `if/then/else` always requires both branches. Use `null` as the else value if no fallback is needed:
```
if condition then result else null
```
