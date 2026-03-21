# 65 — Join Modes (Inner, Left, Right, Full)

## Expression
```
{
  inner: $.orders[*]
    | join $.customers[*] on o => o.customerId equals c => c.id into cust inner
    | select r => { order: r.id, customer: r.cust.name },
  left: $.orders[*]
    | join $.customers[*] on o => o.customerId equals c => c.id into cust left
    | select r => { order: r.id, customer: r.cust.name },
  ...
}
```

## Explanation

### Test data
- Orders: O1→C1, O2→C2, O3→C99 (no matching customer)
- Customers: C1=Alice, C2=Bob, C3=Charlie (no matching order)

### Four join modes

**`inner`** (default) — only matched pairs:
```
O1→Alice, O2→Bob
```
O3 dropped (no customer C99). Charlie dropped (no order).

**`left`** — all left items, matched right or null:
```
O1→Alice, O2→Bob, O3→null
```
All orders kept. O3 has `customer: null` (no match).

**`right`** — all right items, matched left or null:
```
O1→Alice, O2→Bob, null→Charlie
```
All customers kept. Charlie has `order: null` (no matching order).

**`full`** — all items from both sides:
```
O1→Alice, O2→Bob, O3→null, null→Charlie
```
Everything kept. Unmatched sides get `null`.

### Syntax
```
| join source on lKey equals rKey [into alias] [inner|left|right|full]
```

The mode keyword comes after `into` (if present) or after the right key:
```
| join customers on o => o.id equals c => c.id inner        // explicit inner
| join customers on o => o.id equals c => c.id left         // left join
| join customers on o => o.id equals c => c.id into cust right  // with alias + mode
| join customers on o => o.id equals c => c.id              // default = inner
```

### Null-safe property access
When a join produces null values (unmatched sides), accessing properties on null returns `null` instead of throwing:
```
r.cust.name    // returns null if r.cust is null (no error)
```

### SQL equivalents
| Elwood | SQL |
|---|---|
| `\| join ... inner` | `INNER JOIN` |
| `\| join ... left` | `LEFT OUTER JOIN` |
| `\| join ... right` | `RIGHT OUTER JOIN` |
| `\| join ... full` | `FULL OUTER JOIN` |
