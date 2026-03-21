# 64 — Join

## Script
```
let customers = $.customers[*]

$.orders[*]
| join customers on o => o.customerId equals c => c.id into customer
| select o => { orderId: o.id, customerName: o.customer.name, total: o.total }
```

## Explanation
`join` combines two arrays by matching keys — like SQL's INNER JOIN.

### Syntax
```
| join rightSource on leftKey equals rightKey [into alias]
```

- `rightSource` — the array to join with (variable or expression)
- `leftKey` — lambda extracting the join key from each left item
- `rightKey` — lambda extracting the join key from each right item
- `into alias` — optional: nest the matched right item under this name

### How it works
For each order, find the customer whose `id` matches the order's `customerId`:

| Order | customerId | → | Customer | Result |
|---|---|---|---|---|
| O1 | C1 | → | Alice | `{ ...order, customer: Alice }` |
| O2 | C2 | → | Bob | `{ ...order, customer: Bob }` |
| O3 | C1 | → | Alice | `{ ...order, customer: Alice }` |
| O4 | C3 | → | Charlie | `{ ...order, customer: Charlie }` |

### With `into` vs without
**With `into customer`** — the matched right item is nested:
```
{ id: "O1", customerId: "C1", total: 250, customer: { id: "C1", name: "Alice" } }
```

**Without `into`** — properties are merged flat (left wins on conflicts):
```
| join customers on o => o.customerId equals c => c.id
→ { id: "O1", customerId: "C1", total: 250, name: "Alice" }
```

### Performance
Join builds a hash lookup on the right array — O(n+m) not O(n*m). Efficient even for large arrays.

### This was impossible in traditional JSONPath
traditional JSONPath had no join operator. Cross-array lookups required `$$$` parent navigation + `set`/`get` + `cache` — fragile and slow. In Elwood, it's a single pipe operator.

### SQL equivalent
```sql
SELECT o.id, c.name, o.total
FROM orders o
INNER JOIN customers c ON o.customerId = c.id
```
