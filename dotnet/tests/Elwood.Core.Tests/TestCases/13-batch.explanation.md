# 13 — Batch

## Expression
```
$.items[*] | batch 2
```

## Explanation
- `$.items[*]` — all items: `[1, 2, 3, 4, 5]`
- `| batch 2` — split into chunks of size 2 → `[[1, 2], [3, 4], [5]]`

`batch` is useful when you need to process data in fixed-size groups — for example, sending records in batches of 100 to an API.
