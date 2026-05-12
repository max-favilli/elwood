# 99 — Let bindings inside lambda bodies

Verifies that `let` bindings can be used inside lambda bodies (block expressions).

```
| select o =>
  let total = o.qty * o.price
  let label = if total > 20 then "high" else "low"
  { id: o.id, total: total, label: label }
```

Each `let` creates a local variable scoped to the lambda body. Subsequent `let`
bindings can reference earlier ones. The final expression after all `let` bindings
is the lambda's return value.

This enables complex multi-step transformations inside `select`, `where`, and other
pipe operations without needing to hoist everything to the top of the script.
