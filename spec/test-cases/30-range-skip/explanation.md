# 30 — Range + Skip

## Expression
```
range(1, $.items_count) | skip 1
```

## Traditional JSONPath equivalent
```
$.range(1, $.items_count).skip(1)
```

## Explanation
- `range(1, $.items_count)` — generates a sequence of numbers starting at 1, with `$.items_count` (9) elements: `[1, 2, 3, 4, 5, 6, 7, 8, 9]`
- `| skip 1` — skip the first element

Result: `[2, 3, 4, 5, 6, 7, 8, 9]`

### range(start, count)
- `start` — the first number in the sequence
- `count` — how many numbers to generate

Examples:
```
range(0, 5)       → [0, 1, 2, 3, 4]
range(10, 3)      → [10, 11, 12]
range(1, $.n)     → dynamic count from the input data
```

### Use cases
`range` is useful for:
- **Pagination**: generating page numbers from a total count
- **Indexing**: creating numbered sequences for batch processing
- **Generating test data**: producing arrays of sequential values

### Traditional JSONPath difference
In traditional JSONPath, `range` is called as a method on the root: `$.range(1, 9)`. In Elwood, `range` is a standalone function: `range(1, 9)`. Both produce the same result — the function form is cleaner since `range` doesn't logically "belong" to the root JSON object.
