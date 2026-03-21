# 50 — Min / Max

## Expression
```
{
  coldest: $.temperatures[*] | min,
  warmest: $.temperatures[*] | max,
  cheapest: $.orders[*] | select o => o.total | min,
  mostExpensive: $.orders[*] | select o => o.total | max,
  spread: ($.temperatures[*] | max) - ($.temperatures[*] | min)
}
```

## Traditional JSONPath equivalent
```
$.temperatures[*].min()
$.temperatures[*].max()
$.orders[*].total.min()
```

## Explanation
- `| min` — returns the smallest numeric value in the array
- `| max` — returns the largest numeric value in the array

### Direct on numeric arrays
```
$.temperatures[*] | min    → -3.5 (coldest)
$.temperatures[*] | max    → 22.4 (warmest)
```

### With projection
For arrays of objects, first extract the numeric property:
```
$.orders[*] | select o => o.total | min    → 49.5 (cheapest order)
$.orders[*] | select o => o.total | max    → 899 (most expensive)
```

### In calculations
`min` and `max` return numbers, so they can be used in arithmetic:
```
($.temperatures[*] | max) - ($.temperatures[*] | min)    → 25.9 (temperature spread)
```

### Both forms work
```
$.values[*] | min          // pipe operator
$.values[*].min()          // method call (traditional style)
```

### All numeric aggregations
| Operator | Description |
|---|---|
| `\| sum` | Total of all values |
| `\| min` | Smallest value |
| `\| max` | Largest value |
| `\| count` | Number of items |
| `\| first` | First item |
| `\| last` | Last item |
