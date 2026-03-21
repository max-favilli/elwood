# 27 — Length / Count

## Expression
```
$.result[*] | count
```

## Traditional JSONPath equivalent
```
$.result[*].length()
```

## Explanation
- `$.result[*]` — all items in the `result` array
- `| count` — returns the number of items: `6`

### Elwood ways to get length
```
$.result[*] | count              // pipe operator — idiomatic for arrays
someString.length()              // method call — works on strings in chains
items.count()                    // method call — works on arrays in chains
```

### Note on Traditional JSONPath difference
traditional JSONPath returns `[6]` (wrapped in an array) because `.length()` returns `IEnumerable<JToken>`. Elwood returns the plain number `6`.

In traditional JSONPath, `.length()` serves double duty for both arrays and strings. In Elwood, both `count`/`length` work on arrays and strings, but `| count` as a pipe operator is the preferred way for collections.
