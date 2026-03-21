# 07 — Pattern Matching

## Expression
```
$.items[*] | select i => { name: i.name, color: i.status | match "active" => "#00FF00", "retired" => "#FF0000", _ => "#999999" }
```

## Explanation
- `i.status | match` — pipe the status value into a pattern match
- `"active" => "#00FF00"` — if status equals `"active"`, return green
- `"retired" => "#FF0000"` — if status equals `"retired"`, return red
- `_ => "#999999"` — the **wildcard** `_` catches everything else (default case)

Pattern matching replaces deeply nested `if/then/else` chains. Arms are separated by commas. The first matching arm wins.
