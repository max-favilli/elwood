# 58 — Let Replaces Set/Get

## Script
```
let companies = $.foo[*] | select f => f.company

{
  names: $.bar[*] | where b => b.profile.company.in(companies) | select b => b.name
}
```

## Traditional JSONPath equivalent
```
$.set({companies:$.foo[*].company}).toobject({
  names: $.bar[*].where($.profile.company.in($.get($.companies[*])))
})
```

## Explanation
Traditional JSONPath's `.set()` and `.get()` stored and retrieved variables within the expression chain. In Elwood, **`let` bindings replace both entirely**.

### What the expression does
1. Extract all company names from `$.foo` → `["Illumity", "Progenex", "Kage"]`
2. Filter `$.bar` to keep only people whose company is in that list
3. Return their names → `["Alice", "Charlie", "Eve"]`

### Traditional approach
```
$.set({companies: $.foo[*].company})           // store variable
  .toobject({
    names: $.bar[*].where(
      $.profile.company.in($.get($.companies[*]))  // retrieve variable
    )
  })
```
Problems:
- **Chain-scoped**: variables set in one chain are NOT available in another
- **Verbose**: `.set()` / `.get()` syntax is noisy
- **Fragile**: must combine set and get in the same chain
- **`$$$` navigation**: inside `.where()`, `$` refers to the current bar item, so accessing the stored variable requires `$.get()`

### Elwood approach
```
let companies = $.foo[*] | select f => f.company

{ names: $.bar[*] | where b => b.profile.company.in(companies) | select b => b.name }
```
Benefits:
- **Script-scoped**: `companies` is available everywhere after its definition
- **Clean syntax**: just `let name = expression`
- **No retrieval function**: reference by name directly (`companies`, not `$.get($.companies)`)
- **Evaluated once**: computed before the pipeline starts

### Key difference
| | traditional JSONPath `.set()`/`.get()` | Elwood `let` |
|---|---|---|
| Scope | Chain-scoped | Script-scoped |
| Syntax | `.set({key: expr})` / `.get($.key)` | `let key = expr` / `key` |
| Evaluation | Re-evaluated if chain re-enters | Evaluated once |
| Cross-reference | Only within same chain | Available everywhere |
| Need caching? | Often needs `.cache()` too | Never — `let` IS the cache |

### Bottom line
**Never use `.set()` / `.get()` in Elwood.** Use `let` bindings instead — they're simpler, faster, and impossible to scope incorrectly.
