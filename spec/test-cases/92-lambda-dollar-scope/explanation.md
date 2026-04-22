# 92 — Lambda $ scope preservation

When a pipe operation uses an explicit lambda parameter (`c => ...`), `$` must retain
its outer scope value (the input document). Only when using implicit `$` (no lambda
parameter) should `$` be rebound to the current pipe element.

- `first c => c.colorway == $.style` — `c` is the colorway, `$` is the root input
- `where c => c.colorway != $.style` — same
- `where $ > 1` — implicit, `$` is each array element
