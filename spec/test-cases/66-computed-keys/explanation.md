# 66 — Computed Property Keys

## Expression
```
{
  static: "fixed",
  [`{$.prefix}_id`]: $.product.id,
  [$.product.season]: "current",
  attributes: $.fields[*] | reduce (acc, f) => { ...acc, [f.name]: f.value } from {}
}
```

## Explanation
Computed property keys let you use **expressions** as property names, using `[expr]` syntax (like JavaScript):

### Static key (normal)
```
{ name: "Alice" }                    → { "name": "Alice" }
```

### Computed key from path
```
{ [$.product.season]: "current" }    → { "FW26": "current" }
```
The expression `$.product.season` evaluates to `"FW26"`, which becomes the property name.

### Computed key with interpolation
```
{ [`{$.prefix}_id`]: $.product.id }  → { "attr_id": "JW-001" }
```
String interpolation in the key expression builds dynamic property names.

### Building objects dynamically with reduce
```
$.fields[*] | reduce (acc, f) => { ...acc, [f.name]: f.value } from {}
```
This turns an array of `{name, value}` pairs into a single object:
- Start with `{}`
- For each field: spread existing properties, add `[f.name]: f.value`
- Result: `{ "color": "red", "size": "XL", "material": "cotton" }`

### Use cases
- **SAP IDocs**: field names determined by data (`@SEGMENT`, dynamic attribute names)
- **Pivot operations**: turn rows into columns (`[row.key]: row.value`)
- **Dynamic schemas**: property names from configuration or lookup tables
- **Key-value flattening**: `[{k:"a",v:1}, {k:"b",v:2}]` → `{"a":1, "b":2}`

### Syntax
```
{ [expression]: value }              // expression evaluated, result used as key
{ [`template_{$.id}`]: value }       // interpolation in key
{ ...existing, [newKey]: newValue }   // spread + computed key
```
