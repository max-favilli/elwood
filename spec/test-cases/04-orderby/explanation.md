# 04 — OrderBy

## Expression
```
$.users[*] | orderBy u => u.age asc | select u => u.name
```

## Explanation
- `$.users[*]` — all users
- `| orderBy u => u.age asc` — sort by the `age` property in ascending order. Use `desc` for descending
- `| select u => u.name` — extract just the names from the sorted result

Multi-key sorting is supported with commas: `| orderBy u => u.domain asc, u => u.name desc`
