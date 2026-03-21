# 03 — Select with Object Construction

## Expression
```
$.users[*] | select u => { name: u.name, isAdult: u.age >= 18 }
```

## Explanation
- `$.users[*]` — all users
- `| select u =>` — transform each user
- `{ name: u.name, isAdult: u.age >= 18 }` — construct a **new object** with two properties:
  - `name` — copied from the original user
  - `isAdult` — a computed boolean expression

Object literals use `{ key: value }` syntax. Values can be any Elwood expression, including comparisons and arithmetic.
