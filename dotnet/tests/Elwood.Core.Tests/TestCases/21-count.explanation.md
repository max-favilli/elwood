# 21 — Count

## Expression
```
$[*] | count
```

## Traditional JSONPath equivalent
```
$[*].count()
```

## Explanation
- `$[*]` — all items in the root array
- `| count` — returns the number of items

`count` is an **aggregation** operator — it reduces an array to a single number. Other aggregations: `sum`, `min`, `max`, `first`, `last`.

`count` can also appear at any point in a pipeline, e.g. `$.users[*] | where $.active | count` to count only active users.
