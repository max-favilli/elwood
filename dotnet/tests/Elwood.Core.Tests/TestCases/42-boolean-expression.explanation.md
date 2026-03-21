# 42 — Boolean Expressions

## Expression
```
$.products[*] | select p => {
  name: p.name,
  isSpecial: p.price > 10 && (p.category == "simple" || p.type == "seasonal")
}
```

## Traditional JSONPath equivalent
```
$.products[*].select($.toobject({
  name: $.name,
  isSpecial: $.boolean($.price > 10 && ($.category == 'simple' || $.type == 'seasonal'))
}))
```

## Explanation
- `p.price > 10` — price above 10
- `p.category == "simple" || p.type == "seasonal"` — either simple category or seasonal type
- `&&` — both conditions must be true
- Parentheses `()` control precedence: the OR is evaluated before the AND

### Results
| Product | price > 10 | simple OR seasonal | Result |
|---|---|---|---|
| Winter Jacket | ✓ (199) | ✓ (simple) | **true** |
| Socks | ✗ (5) | ✓ (simple) | **false** |
| Hiking Boot | ✓ (149) | ✓ (seasonal) | **true** |
| Cap | ✓ (15) | ✗ (neither) | **false** |

### traditional JSONPath vs Elwood
In traditional JSONPath, `$.boolean(expression)` was needed to wrap a boolean expression — without it, the expression context wouldn't evaluate as a boolean.

In Elwood, **boolean expressions evaluate naturally**. `p.price > 10 && p.category == "simple"` returns `true` or `false` directly — no wrapper function needed. The `boolean()` method is available for explicit coercion of truthy/falsy values:

```
// These are equivalent in Elwood:
p.price > 10 && p.active        // natural boolean expression
boolean(p.price > 10 && p.active)  // explicit (traditional style)

// Coerce truthy values:
boolean(p.name)     // true if name is non-empty, false if null/empty
boolean(0)          // false
boolean("")         // false
boolean(null)       // false
boolean("hello")    // true
boolean(42)         // true
```
