# 107 — Trailing `?` optional access in function argument

`obj.prop?` should be valid anywhere — including inside function call arguments.
The `?` completes the expression, and `)` is the next token in the outer context.
