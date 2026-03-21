# 11 — String Interpolation

## Expression
```
$.users[*] | select u => `{u.name} ({u.role})`
```

## Explanation
- Backtick strings (`` ` ``) support **interpolation** — embed any Elwood expression inside `{...}`
- `{u.name}` — evaluates to the user's name
- `{u.role}` — evaluates to the user's role
- The surrounding text is literal

Result for Alice: `"Alice (admin)"`. Any valid Elwood expression can appear inside `{}`, including pipes, method calls, and arithmetic.
