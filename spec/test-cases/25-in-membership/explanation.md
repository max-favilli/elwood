# 25 — In (Membership Check)

## Script
```
let allowed = $.allowedSkus[*]
let extras = $.extraCodes[*]

return {
  fromArray: $.products[*] | where p => p.sku.in(allowed) | select p => p.name,
  fromLiteral: $.products[*] | where p => p.sku.in(["ZWSP", "PR00", "ZRRP"]) | select p => p.name,
  fromMixed: $.products[*] | where p => p.sku.in(allowed, ["XTRA"], "NONE") | select p => p.name
}
```

## Traditional JSONPath equivalents
```
$[*].where($.sku.in($$$$$.allowedSkus[*]))
$[*].where($.sku.in(['ZWSP','PR00','ZRRP']))
$[*].where($.sku.in($$$$$.allowedSkus[*], $$$$$.extraCodes[*]))
```

## Explanation
`.in()` checks if a value exists in a collection. It accepts **multiple arguments** of any type:

- **Array variable**: `.in(allowed)` — checks against the `allowed` array
- **Inline array literal**: `.in(["ZWSP", "PR00", "ZRRP"])` — checks against a literal list
- **Multiple mixed arguments**: `.in(allowed, ["XTRA"], "NONE")` — checks against the **union** of an array variable, an inline array, and a scalar value

### How arguments are combined
All arguments are **flattened**: arrays are expanded, scalars are included directly. So:
```
value.in(["a", "b"], "c", ["d"])
```
is equivalent to checking if `value` is in `["a", "b", "c", "d"]`.

### Traditional JSONPath comparison
In traditional JSONPath, `.in()` requires parent navigation (`$$$$$`) to access sibling arrays from inside a loop. In Elwood, `let` bindings make the reference clean and readable:
```
let allowed = $.allowedSkus[*]     // computed once
$.products[*] | where p => p.sku.in(allowed)   // used inside loop
```
