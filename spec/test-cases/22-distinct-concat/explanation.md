# 22 — Distinct + Concat

## Expression
```
$.bar[*] | distinct | concat
```

## Traditional JSONPath equivalent
```
$.bar[*].distinct().concat()
```

## Explanation
- `$.bar[*]` — all items in the `bar` array: `[0, 1, 2, 3, 4, 2, 6, 8, 8, 2]`
- `| distinct` — remove duplicates → `[0, 1, 2, 3, 4, 6, 8]`
- `| concat` — join all values into a single string with the default separator `|`

Result: `"0|1|2|3|4|6|8"`

### Separator
`concat` uses `|` as the default separator (matching traditional behavior). To use a different separator:
```
$.bar[*] | distinct | concat(",")    → "0,1,2,3,4,6,8"
$.bar[*] | distinct | concat("; ")   → "0; 1; 2; 3; 4; 6; 8"
```

### Note on Traditional JSONPath difference
traditional JSONPath returns `["0|1|2|3|4|6|8"]` (wrapped in an array) because its internal pipeline uses `IEnumerable<JToken>`. Elwood returns the plain string `"0|1|2|3|4|6|8"` — the array wrapping was a v1 implementation artifact, not meaningful semantics.
