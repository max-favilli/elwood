# 26 — Index

## Expression
```
$.foo[*] | index
```

## Traditional JSONPath equivalent
```
$.foo[*].index()
```

## Explanation
- `$.foo[*]` — all items in the `foo` array (3 items)
- `| index` — replaces each item with its **0-based position** in the array

Result: `[0, 1, 2]`

### Use cases
`index` is useful when you need to number items or use their position in calculations:
```
$.items[*] | select (item, i) => { position: i, value: item }   // future: indexed select
$.items[*] | index                                                // just the indices
```

### Note
In traditional JSONPath, `.index()` is called as a method on each element and returns that element's position within its parent array. In Elwood, `| index` is a pipe operator that transforms the array into an array of indices.
