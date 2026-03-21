# 53 — Array Slice

## Expression
```
{
  slice2to5: $.items[2:5],
  first3: $.items[:3],
  from5: $.items[5:],
  lastTwo: $.items[-2:]
}
```

## Traditional JSONPath equivalent
```
$.items[2:5]      // standard JSONPath slice
```

## Explanation
Array slice `[start:end]` extracts a sub-array efficiently without materializing the full array:

| Syntax | Meaning | Result |
|---|---|---|
| `[2:5]` | Elements 2, 3, 4 (end exclusive) | `["c", "d", "e"]` |
| `[:3]` | First 3 elements | `["a", "b", "c"]` |
| `[5:]` | From index 5 to end | `["f", "g", "h"]` |
| `[-2:]` | Last 2 elements | `["g", "h"]` |

### Slice vs skip/take
Both achieve the same result, but slice is more concise and faster:
```
$.items[2:5]                    // slice — single operation
$.items[*] | skip 2 | take 3    // pipe — two operations, intermediate array
```

Use slice for simple index-based extraction. Use `skip`/`take` when the offset or count is computed dynamically.

### Negative indices
Negative values count from the end:
- `[-1:]` — last element
- `[-3:]` — last 3 elements
- `[:-2]` — everything except last 2
