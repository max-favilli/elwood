# 05 — GroupBy with Count

## Expression
```
$.users[*] | groupBy u => u.role | select g => { role: g.key, count: g.items | count }
```

## Explanation
- `$.users[*]` — all users
- `| groupBy u => u.role` — group users by their `role` property. Each group becomes an object with:
  - `.key` — the group key (e.g. `"admin"`)
  - `.items` — the array of items in that group
- `| select g =>` — transform each group
- `g.items | count` — pipe the group's items into `count` to get the number of items in each group

`groupBy` is powerful for aggregation — you can chain any pipe operations on `.items` inside the select.
