# 62 — Sanitize & Null/Empty Checks

## Expression
```
{
  sanitizedGerman: $.german.sanitize(),
  sanitizedGreek: $.greek.sanitize(),
  sanitizedAccent: $.accented.sanitize(),
  checks: {
    presentEmpty: $.values.present.isNullOrEmpty(),
    emptyEmpty: $.values.empty.isNullOrEmpty(),
    wsEmpty: $.values.whitespace.isNullOrEmpty(),
    wsWhitespace: $.values.whitespace.isNullOrWhiteSpace(),
    nullEmpty: $.values.nothing.isNullOrEmpty()
  },
  fallbacks: {
    present: $.values.present.isNullOrEmpty("default"),
    empty: $.values.empty.isNullOrEmpty("default"),
    nothing: $.values.nothing.isNullOrEmpty("default")
  }
}
```

## Explanation

### sanitize()
Transliterates special characters to ASCII equivalents:
- German: `ß` → `ss`, umlauts removed (`ü` → `u`, `ö` → `o`)
- Greek: `Α` → `A`, `θ` → `th`, `Φ` → `Ph`
- Accented: `é` → `e`, `ï` → `i`, `ç` → `c`

```
"Straße München".sanitize()    → "Strasse Munchen"
"café résumé".sanitize()       → "cafe resume"
```

Useful for generating ASCII-safe IDs, filenames, or search keys from international text.

### Null/empty checks — boolean mode (no args)
| Method | `"hello"` | `""` | `"   "` | `null` | `[]` |
|---|---|---|---|---|---|
| `.isNull()` | false | false | false | **true** | false |
| `.isEmpty()` | false | **true** | false | **true** | **true** |
| `.isNullOrEmpty()` | false | **true** | false | **true** | **true** |
| `.isNullOrWhiteSpace()` | false | **true** | **true** | **true** | **true** |

### Null/empty checks — fallback mode (with arg)
When called with an argument, returns the **fallback value** if empty, or the **original value** if not:
```
"hello".isNullOrEmpty("default")    → "hello"     // not empty → original
"".isNullOrEmpty("default")         → "default"   // empty → fallback
null.isNullOrEmpty("default")       → "default"   // null → fallback
```

This replaces the traditional JSONPath pattern of `.isNullOrEmpty(fallbackPath)` — a compact way to provide default values.
