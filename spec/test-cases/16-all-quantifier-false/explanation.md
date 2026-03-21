# 16 — All Quantifier (false case)

## Expression
```
$[*] | all u => u.profile.company == "Illumity"
```

## Traditional JSONPath equivalent
```
$[*].All('{$.profile.company}' = 'Illumity')
```

## Explanation
- `| all u => u.profile.company == "Illumity"` — checks if **every** item has company `"Illumity"`
- Only Vicki Richard's company is `"Illumity"`, so the result is `false`

Note the difference from traditional JSONPath: the v1 expression uses string interpolation `'{$.profile.company}'` and single `=` for equality. In Elwood, the lambda gives direct access to the value — no interpolation needed — and `==` is the equality operator.
