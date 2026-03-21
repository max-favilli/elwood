# 45 — Ceiling

## Expression
```
$.Qty.toNumber().ceiling()
```

## Traditional JSONPath equivalent
```
$.Qty.Ceiling()
```

## Explanation
- `$.Qty` → `"5.6"` (a string in the JSON)
- `.toNumber()` → `5.6` (convert to number)
- `.ceiling()` → `6` (round up to nearest integer)

### Note on type conversion
In traditional JSONPath, `.Ceiling()` implicitly converts the string to a number. In Elwood, the conversion is explicit with `.toNumber()` — making it clear that the input is a string being treated as a number. This prevents surprises when the value isn't a valid number.

### Rounding methods
| Method | Input | Result | Description |
|---|---|---|---|
| `.ceiling()` | 5.6 | 6 | Round up |
| `.floor()` | 5.6 | 5 | Round down |
| `.round()` | 5.6 | 6 | Round to nearest |
| `.round(1)` | 5.65 | 5.7 | Round to N decimals |
| `.abs()` | -5.6 | 5.6 | Absolute value |
