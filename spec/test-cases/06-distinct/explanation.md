# 06 — Distinct

## Expression
```
$.users[*] | select u => u.role | distinct
```

## Explanation
- `$.users[*]` — all users
- `| select u => u.role` — extract just the `role` from each user (produces `["admin", "user", "admin"]`)
- `| distinct` — remove duplicate values, keeping only unique ones

`distinct` compares values by content, so two strings `"admin"` are considered equal. It also works on objects and arrays (compared by deep equality).
