# 34 — Where with Complex Boolean Expression

## Script
```
let allowed = $.allowedCities[*]

$.people[*]
| where p => p.name.length() > 5 && p.age == 18 && p.city.in(allowed)
| select p => { name: p.name, city: p.city }
```

## Traditional JSONPath equivalent
```
$.people[*].where($.name.length() > 5 && $.age == 18 && $.city.in($$$$$.allowedCities[*]))
```

## Explanation
The `where` predicate combines three conditions with `&&`:

1. **`p.name.length() > 5`** — name longer than 5 characters (method call inside predicate)
2. **`p.age == 18`** — age equals 18
3. **`p.city.in(allowed)`** — city is in the allowed list (`.in()` with a `let`-bound array)

All three must be true for a person to pass the filter.

### Who gets filtered out and why
| Name | length > 5 | age == 18 | city in allowed | Result |
|---|---|---|---|---|
| Maximilian | ✓ (10) | ✓ | ✓ (Berlin) | **PASS** |
| Anna | ✗ (4) | ✓ | ✓ (Munich) | fail |
| Bob | ✗ (3) | ✗ (25) | ✓ (Berlin) | fail |
| Christopher | ✓ (11) | ✓ | ✗ (Vienna) | fail |
| Li | ✗ (2) | ✓ | ✓ (Hamburg) | fail |
| Alexander | ✓ (9) | ✗ (30) | ✓ (Munich) | fail |
| Margarethe | ✓ (10) | ✓ | ✓ (Berlin) | **PASS** |

### Traditional JSONPath comparison
The key difference is how the `allowedCities` array is referenced:
- **traditional JSONPath**: `$$$$$.allowedCities[*]` — count parent hops from inside the where context back to root
- **Elwood**: `let allowed = $.allowedCities[*]` — pre-compute once, reference by name

This is cleaner, faster (no repeated evaluation), and impossible to get wrong (no `$` counting).

### Combining conditions
Elwood supports full boolean logic in predicates:
```
p.x > 0 && p.y > 0              // AND
p.x > 0 || p.y > 0              // OR
!(p.x > 0)                       // NOT
(p.x > 0 || p.y > 0) && p.z     // grouped with parentheses
```
