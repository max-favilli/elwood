# 60 — ConvertTo

## Expression
```
{
  toInt: $.qty.convertTo("Int32"),
  toDouble: $.qty.convertTo("Double"),
  toBoolFromFlag: $.flag.convertTo("Boolean"),
  toBoolFromString: $.active.convertTo("Boolean"),
  boolArray: $.values[*] | select v => v.convertTo("Boolean")
}
```

## Traditional JSONPath equivalent
```
$.qty.convertTo('Int32')
$.qty.convertTo('Double')
$.flag.convertTo('Boolean')
$.values[*].convertTo('Boolean')
```

## Explanation
`.convertTo(type)` converts a value to the specified .NET type.

### Supported types
| Type name | Aliases | Example |
|---|---|---|
| `"Int32"` | `"int"`, `"integer"` | `"5.600"` → `5` (truncated) |
| `"Int64"` | `"long"` | `"5.600"` → `5` |
| `"Double"` | `"float"`, `"decimal"` | `"5.600"` → `5.6` |
| `"Boolean"` | `"bool"` | `"1"` → `true`, `"0"` → `false` |
| `"String"` | | `42` → `"42"` |

### Boolean conversion rules
Matching traditional behavior:
| Input | Result | Why |
|---|---|---|
| `"42"` | `true` | Parsed as number 42 ≠ 0 |
| `"3.14"` | `true` | Parsed as number 3.14 ≠ 0 |
| `"true"` | `true` | Boolean literal |
| `"0"` | `false` | Parsed as number 0 |
| `""` | `false` | Empty string |
| `"hello"` | `true` | Non-empty, non-numeric string |

### Elwood alternatives
For simple conversions, Elwood has dedicated methods:
```
$.qty.toNumber()              // same as convertTo("Double")
$.flag.boolean()              // same as convertTo("Boolean") with truthiness
$.value.toString()            // same as convertTo("String")
```

`.convertTo()` is useful when the target type comes from the traditional JSONPath naming convention or when you need specific integer truncation behavior.
