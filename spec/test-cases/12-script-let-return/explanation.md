# 12 — Script with Let Bindings

## Script
```
let adults = $.users[*] | where u => u.age >= 18
let admins = adults | where u => u.role == "admin"
return { adultCount: adults | count, adminNames: admins | select u => u.name }
```

## Explanation
- `let adults = ...` — compute the filtered array once and bind it to the name `adults`
- `let admins = adults | ...` — reuse `adults` (already computed) and filter further
- `return { ... }` — build the final output object

**`let` bindings** are evaluated once and are available to all subsequent bindings and the return expression. This is fundamentally different from Traditional JSONPath's chain-scoped `set`/`get` — variables are **script-scoped**, not chain-scoped.

Scripts use `let` + `return`. Single expressions don't need either.
