# Migration Patterns — From Traditional Maps to Elwood Scripts

This guide shows how common transformation map patterns translate to Elwood scripts.

---

## Conditional Properties

Traditional map systems use a `conditions` field to include or exclude entire sections of the output based on runtime data. Elwood scripts have two patterns for this, depending on whether you want the property to be `null` or completely absent.

### Pattern 1: Property is null when condition is false

Use `if/then/else null`. The property exists in the output but has a `null` value.

```elwood
{
  name: $.name,
  matHead: if $.hasMatHead then {
    "@SEGMENT": "1",
    MATL_TYPE: $.SAP_STUFF.MATL_TYPE,
    MATL_GROUP: $.LOOKUP.CWG_MATERIAL_GROUP
  } else null
}
```

Output when `$.hasMatHead` is true:
```json
{ "name": "...", "matHead": { "@SEGMENT": "1", "MATL_TYPE": "...", ... } }
```

Output when `$.hasMatHead` is false:
```json
{ "name": "...", "matHead": null }
```

**When to use:** When downstream systems accept null values, or when you want a consistent output shape (same properties always present).

### Pattern 2: Property is completely absent when condition is false

Use the spread operator with a conditional: `...if cond then { key: value } else {}`. When the condition is false, an empty object is spread — adding nothing.

```elwood
{
  name: $.name,
  ...if $.hasMatHead then {
    matHead: {
      "@SEGMENT": "1",
      MATL_TYPE: $.SAP_STUFF.MATL_TYPE,
      MATL_GROUP: $.LOOKUP.CWG_MATERIAL_GROUP
    }
  } else {}
}
```

Output when `$.hasMatHead` is true:
```json
{ "name": "...", "matHead": { "@SEGMENT": "1", "MATL_TYPE": "...", ... } }
```

Output when `$.hasMatHead` is false:
```json
{ "name": "..." }
```

The `matHead` property does not exist at all. This matches the behavior of traditional map `conditions` where the entire node is skipped.

**When to use:** When the output must not contain the property at all (e.g., XML generation where absent elements differ from null elements, or strict schema validation).

### Multiple conditional properties

Both patterns compose naturally:

```elwood
{
  name: $.name,
  ...if $.SAP_STUFF.BRAND == "JW" then { jwGroup: $.JW_PRODUCTGROUP } else {},
  ...if $.SAP_STUFF.BRAND == "TM" then { tmType: $.TM_STY_TYPE } else {},
  ...if $.hasExtended then {
    extendedData: {
      field1: $.EXT.FIELD1,
      field2: $.EXT.FIELD2
    }
  } else {}
}
```

---

## Conditional Array Items

When building an array where some items should be included or excluded based on conditions.

### Filter with where

The simplest approach — build the full array, then filter:

```elwood
let characteristics = [
  { name: "C8_PRODUCTSUBTYPE", value: $.C8_PRODUCTSUBTYPE },
  { name: "JW_PRODUCTGROUP", value: $.JW_PRODUCTGROUP },
  { name: "TM_STY_TYPE", value: $.TM_STY_TYPE }
]

characteristics | where c => c.value.isNullOrEmpty().not()
```

### Conditional with memo and filter

For brand-filtered characteristics (like SAP AUSPRT segments):

```elwood
let ausprt = memo (src, charName) =>
  if src.isNullOrEmpty() then null
  else { "@SEGMENT": "1", FUNCTION: "009", CHAR_NAME: charName, VALUE: src.CHAR_VALUE_LONG }

let ausprtJW = memo (src, charName) =>
  if $.SAP_STUFF.BRAND != "JW" then null
  else ausprt(src, charName)

let ausprtTM = memo (src, charName) =>
  if $.SAP_STUFF.BRAND != "TM" then null
  else ausprt(src, charName)

// Build the array — nulls are filtered out
let allSegments = [
  ausprtJW($.C8_PRODUCTSUBTYPE, "C8_PRODUCTSUBTYPE"),
  ausprtJW($.JW_PRODUCTGROUP, "JW_PRODUCTGROUP"),
  ausprtTM($.TM_STY_TYPE, "TM_STY_TYPE"),
  ausprtTM($.TM_FRANCHISE, "TM_FRANCHISE")
] | where s => s != null

return { E1BPE1AUSPRT: allSegments }
```

---

## Reusable Sub-Transformations with `let`

Traditional maps duplicate structure when the same pattern appears multiple times. Elwood scripts use `let` bindings.

### Traditional approach (repetitive)
```
source: $.sku[*]    destination: E1BPE1MARMRT    map: [{ source: "1", dest: "@SEGMENT" }, { source: "$.FUNCTION", dest: "FUNCTION" }, ...]
source: $.sku[*]    destination: E1BPE1MARMRTX   map: [{ source: "1", dest: "@SEGMENT" }, { source: "$.FUNCTION", dest: "FUNCTION" }, ...]
```

### Elwood approach (reusable)
```elwood
let marmrt = memo s => {
  "@SEGMENT": "1",
  FUNCTION: s.FUNCTION,
  MATERIAL_LONG: s.MATERIAL_LONG,
  VARIANT_LONG: s.VARIANT_LONG,
  ALT_UNIT_ISO: s.ALT_UNIT_ISO
}

let marmrtx = memo s => {
  ...marmrt(s),
  MATERIAL_LONG: "X",
  VARIANT_LONG: "X"
}

return {
  E1BPE1MARMRT: $.sku[*].E1BPE1MARMRT[*] | select s => marmrt(s),
  E1BPE1MARMRTX: $.sku[*].E1BPE1MARMRT[*] | select s => marmrtx(s)
}
```

---

## Static/Constant Values

Traditional maps use literal strings as the source value. In Elwood, literals are just values in the object:

```elwood
// Traditional: { source: "1", destination: "@SEGMENT" }
// Elwood:
{
  "@SEGMENT": "1",
  TABNAM: "EDI_DC_40",
  MANDT: "400",
  STATUS: "53"
}
```

---

## Iteration (treatAsObject / arrays)

Traditional maps use `source: "$.items[*]"` with `treatAsObject: true/false` to control array vs object output.

### Array output (treatAsObject: false)
```elwood
// Each source item becomes an array element
$.sku[*] | select s => {
  "@SEGMENT": "1",
  MATERIAL_LONG: s.MATERIAL_LONG,
  VARIANT_LONG: s.VARIANT_LONG
}
```

### Single object output (treatAsObject: true)
```elwood
// Merge into one object (typically when source is $, not an array)
{
  "@SEGMENT": "1",
  MATL_TYPE: $.SAP_STUFF.MATL_TYPE,
  MATL_GROUP: $.LOOKUP.CWG_MATERIAL_GROUP
}
```

---

## NullValueHandling

Traditional maps have `nullValueHandling: "Ignore"` (skip property if null) vs `"Include"` (keep it).

### Ignore (skip null properties)
```elwood
// Use the conditional spread pattern
{
  name: $.name,
  ...if $.email != null then { email: $.email } else {}
}
```

### Include (keep null properties)
```elwood
// Default behavior — just assign it
{
  name: $.name,
  email: $.email     // will be null if $.email is null
}
```

### Ignore with fallback (defaultValue)
```elwood
{
  name: $.name,
  email: $.email.isNullOrEmpty("N/A")    // fallback to "N/A" if null/empty
}
```

---

## Parent Navigation ($ vs $$$ vs $$$$$)

Traditional JSONPath systems use `$$$` to navigate up the parent hierarchy. This is fragile and hard to count.

Elwood replaces this entirely with `let` bindings and lambda parameters:

```elwood
// Traditional: $$$$$.SAP_STUFF.MATERIAL_LONG (count 5 parents up from inside a loop)
// Elwood: just reference it directly — $ is always root, lambda params name the current item

let materialLong = $.SAP_STUFF.MATERIAL_LONG

$.sku[*] | select s => {
  MATERIAL_LONG: materialLong,     // from root via let binding
  VARIANT_LONG: s.SAP_STUFF.VARIANT_LONG   // from current item via lambda param
}
```

---

## Summary: Pattern Reference

| Traditional Map Pattern | Elwood Equivalent |
|---|---|
| `conditions: [{ path, values }]` | `if cond then value else null` or `...if cond then { key: val } else {}` |
| `source: "$.items[*]"` + child map | `$.items[*] \| select i => { ... }` |
| `source: "staticValue"` | Direct literal: `KEY: "staticValue"` |
| `treatAsObject: true` | Object literal: `{ key: $.path }` |
| `treatAsObject: false` | `\| select` pipeline returns array |
| `nullValueHandling: "Ignore"` | `...if $.val != null then { key: $.val } else {}` |
| `nullValueHandling: "Include"` | `key: $.val` (null stays) |
| `defaultValue: "N/A"` | `$.val.isNullOrEmpty("N/A")` |
| `$$$$$.path` (parent navigation) | `let x = $.path` or lambda param |
| Repeated map structure | `let fn = memo s => { ... }` |
| `.cache("key")` | `let` binding (computed once) or `memo` (computed once per distinct arg) |
