# 44 — Not (Boolean Negation)

## Expression
```
{
  barNot: $.bar.not(),
  arrayNot: $.foo[*] | select v => v.not()
}
```

## Traditional JSONPath equivalent
```
$.bar.not()
$.foo[*].not()
```

## Explanation
`.not()` negates the **truthiness** of a value:

| Value | Truthy? | `.not()` |
|---|---|---|
| `1` | yes | `false` |
| `false` | no | `true` |
| `2` | yes | `false` |
| `0` | no | `true` |
| `null` | no | `true` |
| `""` | no | `true` |
| `true` | yes | `false` |
| `"abc"` | yes | `false` |

### Truthiness rules in Elwood
- **Truthy**: `true`, non-zero numbers, non-empty strings, non-empty arrays, objects
- **Falsy**: `false`, `0`, `null`, `""` (empty string), `[]` (empty array)

### `.not()` vs `!` operator
Both are equivalent:
```
$.bar.not()         // method call
!$.bar              // prefix operator
```

Use `.not()` in method chains, `!` in boolean expressions:
```
$.items[*] | where v => !v.active        // operator style
$.items[*] | select v => v.active.not()  // method style
```

### traditional behavior difference
Traditional JSONPath's `.not()` only considers boolean `true` as truthy — numbers like `1` and `2` return `true` from `.not()`. Elwood follows standard truthiness rules (matching JavaScript, Python), where non-zero numbers and non-empty strings are truthy. This is the more consistent and widely expected behavior.
