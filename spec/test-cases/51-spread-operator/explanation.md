# 51 — Spread Operator

## Script
```
let defaults = $.defaults

$.orders[*] | select o => {
  ...o,
  ...defaults,
  total: o.price * o.qty,
  processed: true
}
```

## Explanation
The spread operator `...` copies all properties from an object into a new object literal, just like JavaScript:

- `...o` — copies all properties from the order (`id`, `sku`, `qty`, `price`)
- `...defaults` — copies all properties from defaults (`warehouse`, `currency`)
- `total: o.price * o.qty` — adds a computed property
- `processed: true` — adds a static property

### Result
Each order gets enriched with default values and computed fields:
```json
{
  "id": "A1", "sku": "JW-001", "qty": 2, "price": 199.99,
  "warehouse": "EU-Central", "currency": "EUR",
  "total": 399.98, "processed": true
}
```

### Property order and overrides
Properties are applied in order. Later properties override earlier ones:
```
{ ...original, status: "updated" }    // overrides original.status if it exists
{ status: "default", ...override }    // override.status wins if present
```

### Traditional JSONPath comparison
traditional JSONPath doesn't have a spread operator. To achieve the same result, you'd need to list every property explicitly in a `.toobject()` call, or use `.keep()` / `.remove()` to manipulate properties. The spread operator makes this dramatically simpler.

### Common patterns
```
// Add a property
{ ...item, newField: "value" }

// Override a property
{ ...item, status: "processed" }

// Merge two objects
{ ...base, ...overrides }

// Pick properties and add computed ones
{ ...o, total: o.price * o.qty }
```
