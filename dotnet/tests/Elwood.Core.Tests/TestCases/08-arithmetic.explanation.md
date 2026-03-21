# 08 — Arithmetic

## Expression
```
$.price * (1 + $.tax)
```

## Explanation
- `$.price` — access the `price` property (100)
- `$.tax` — access the `tax` property (0.21)
- `1 + $.tax` — compute 1.21
- `$.price * (...)` — multiply price by (1 + tax) = 121

Standard arithmetic operators (`+`, `-`, `*`, `/`) are supported with normal precedence rules. Parentheses override precedence. The `+` operator also concatenates strings.
