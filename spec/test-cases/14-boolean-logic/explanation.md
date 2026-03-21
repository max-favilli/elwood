# 14 — Boolean Logic

## Expression
```
$.users[*] | where u => u.age > 18 && u.active == true | select u => u.name
```

## Explanation
- `u.age > 18 && u.active == true` — combine two conditions with `&&` (logical AND)
- Both must be true for the user to pass the filter

Supported logical operators:
- `&&` — AND (both sides must be true)
- `||` — OR (either side can be true)
- `!` — NOT (prefix negation, e.g. `!u.active`)

Comparison operators: `==`, `!=`, `<`, `<=`, `>`, `>=`
