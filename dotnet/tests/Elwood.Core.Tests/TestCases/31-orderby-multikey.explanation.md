# 31 — OrderBy Multi-Key

## Expression
```
$.employees[*]
| orderBy e => e.department asc, e => e.salary desc, e => e.name asc
| select e => { name: e.name, dept: e.department, salary: e.salary }
```

## Traditional JSONPath equivalent
```
$.employees[*].sort(asc, $.department, desc, $.salary, asc, $.name)
```

## Explanation
Multi-key sorting applies keys in priority order:

1. **`e.department asc`** — first, sort by department alphabetically (Engineering before Marketing)
2. **`e.salary desc`** — within the same department, sort by salary highest-first
3. **`e.name asc`** — within the same department and salary, sort by name alphabetically

### Result breakdown
```
Engineering (asc) → sorted by salary desc:
  Charlie  110000    ← highest salary
  Alice     95000    ← tied salary, sorted by name asc
  Eve       95000    ← tied salary, sorted by name asc

Marketing (asc) → sorted by salary desc:
  Diana     85000
  Bob       72000    ← tied salary, sorted by name asc
  Frank     72000    ← tied salary, sorted by name asc
```

### Syntax
Each sort key is a comma-separated pair of `expression direction`:
```
| orderBy x => x.key1 asc, x => x.key2 desc, x => x.key3 asc
```

Directions:
- `asc` — ascending (A→Z, 0→9) — default if omitted
- `desc` — descending (Z→A, 9→0)

### Traditional JSONPath comparison
traditional JSONPath alternates direction and path: `.sort(asc, $.field, desc, $.field2)`. In Elwood, each key has its own lambda and direction, making it explicit which expression maps to which sort order.
