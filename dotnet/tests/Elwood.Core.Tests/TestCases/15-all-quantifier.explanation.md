# 15 — All Quantifier

## Expression
```
$[*] | all u => u.profile.company != "000Illumity"
```

## Traditional JSONPath equivalent
```
$[*].All($.profile.company != '000Illumity')
```

## Explanation
- `$[*]` — all items in the root array
- `| all u => ...` — returns `true` if the predicate is true for **every** item
- `u.profile.company != "000Illumity"` — check that the company is not "000Illumity"

Since no item has company `"000Illumity"`, the result is `true`.

`all` is a quantifier — it reduces an array to a single boolean. See also `any` (true if at least one item matches).
