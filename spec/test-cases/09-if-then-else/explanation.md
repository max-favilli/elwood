# 09 — If / Then / Else

## Expression
```
$.users[*] | select u => { name: u.name, label: if u.age >= 18 then "adult" else "minor" }
```

## Explanation
- `if u.age >= 18 then "adult" else "minor"` — a ternary conditional expression:
  - If the condition is true → returns `"adult"`
  - Otherwise → returns `"minor"`

`if/then/else` is an **expression** (it returns a value), not a statement. It can be used anywhere a value is expected — inside object literals, as function arguments, in pipe operations, etc.
