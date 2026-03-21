# 54 — Concat (Extended)

## Expression
```
{
  default: $.names[*] | concat,
  comma: $.names[*].concat(", "),
  merged: $.names[*].concat("|", $.extras[*], [1, 2])
}
```

## Traditional JSONPath equivalents
```
$.names[*].concat()
$.names[*].concat(', ')
$.names[*].concat('|', $$$.extras[*], [1,2])
```

## Explanation

### Three forms of concat

**1. No args — default separator `|`:**
```
$.names[*] | concat          → "Alice|Bob|Charlie"
```

**2. Custom separator:**
```
$.names[*].concat(", ")     → "Alice, Bob, Charlie"
```

**3. Merge additional arrays before joining:**
```
$.names[*].concat("|", $.extras[*], [1, 2])
```
This merges:
- `$.names[*]` → `["Alice", "Bob", "Charlie"]`
- `$.extras[*]` → `["Diana", "Eve"]`
- `[1, 2]` → `[1, 2]`

Then joins everything with `|` → `"Alice|Bob|Charlie|Diana|Eve|1|2"`

### Pipe vs method form
```
$.items[*] | concat              // pipe — separator only
$.items[*] | concat(", ")       // pipe — custom separator
$.items[*].concat("|", extra)    // method — merge + join (use for multi-source)
```

The pipe form covers simple cases. The method form (`.concat(sep, ...additional)`) supports merging multiple arrays into one joined string.
