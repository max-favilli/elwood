# 02 — Where Filter

## Expression
```
$.users[*] | where u => u.age > 18 | select u => u.name
```

## Explanation
- `$.users[*]` — select all items from the `users` array
- `| where u => u.age > 18` — pipe into a `where` filter; for each item (named `u`), keep it only if `u.age > 18`
- `| select u => u.name` — pipe the filtered results into a `select` projection; for each item, extract just the `name`

The `|` (pipe) operator passes the output of one step as the input to the next, like a data pipeline. The `u =>` syntax is a **lambda expression** that names the current item `u` so you can access its properties.
