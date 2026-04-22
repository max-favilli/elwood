# 93 — Dynamic index expressions

Verifies that bracket index access `[expr]` works with variables and expressions,
not just literal integers. The path parser now backtracks on non-literal bracket
contents, letting the postfix parser handle `[expr]` as an IndexExpression.

- `$.items[idx]` — variable index
- `$.items[$.pick]` — field-based index
- `$.items[$.nested.idx]` — nested field index
- `items[idx]` — let-binding + variable index
- `$.items[*] | index | select i => $.items[i]` — lambda variable index
