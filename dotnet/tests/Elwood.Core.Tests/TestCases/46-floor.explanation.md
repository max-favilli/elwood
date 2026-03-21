# 46 — Floor

## Expression
```
$.Qty.toNumber().floor()
```

## Traditional JSONPath equivalent
```
$.Qty.floor()
```

## Explanation
- `$.Qty` → `"5.6"` (string)
- `.toNumber()` → `5.6`
- `.floor()` → `5` (round down to nearest integer)

See test 45 (ceiling) for a comparison of all rounding methods.
