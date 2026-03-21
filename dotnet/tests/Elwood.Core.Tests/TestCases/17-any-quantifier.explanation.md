# 17 — Any Quantifier

## Expression
```
$[*] | any u => u.profile.company == "Illumity"
```

## Traditional JSONPath equivalent
```
$[*].Any('{$.profile.company}' = 'Illumity')
```

## Explanation
- `| any u => ...` — returns `true` if the predicate is true for **at least one** item
- Vicki Richard's company is `"Illumity"`, so the result is `true`

`any` is the counterpart to `all`:
- `all` — every item must match (logical AND across items)
- `any` — at least one item must match (logical OR across items)
