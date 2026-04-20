# Member access auto-map over arrays

Regression test: when a variable holds an array (from `let edges = $.edges[*].node`), subsequent property access like `edges[*].name` must auto-map over the array — the `.name` goes through MemberAccess, not PathExpression.

1. **`edges[*].name`** — auto-maps `.name` over each node in the array
2. **`edges[*].variant?.sku`** — combines auto-mapping with optional chaining; null variants are skipped by the `?.` + filter
3. **Bug this prevents** — without auto-mapping in MemberAccess, `edges[*].name` would return null because LazyArrayValue.GetProperty returns null for all properties
