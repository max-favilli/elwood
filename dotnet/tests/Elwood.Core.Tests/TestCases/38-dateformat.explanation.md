# 38 — Date Format

## Expression
```
$.date.dateFormat("yyyy MMMM dd HH:mm:ss")
```

## Traditional JSONPath equivalent
```
$.date.dateFormat('yyyy MMMM dd HH:mm:ss')
```

## Explanation
- `$.date` → `"2025-10-10T11:15:16.4000727Z"` (ISO 8601 datetime with fractional seconds)
- `.dateFormat("yyyy MMMM dd HH:mm:ss")` → format using .NET format specifiers

Result: `"2025 October 10 11:15:16"`

### Format specifiers
| Specifier | Meaning | Example |
|---|---|---|
| `yyyy` | 4-digit year | `2025` |
| `MMMM` | Full month name | `October` |
| `MMM` | Abbreviated month | `Oct` |
| `MM` | 2-digit month | `10` |
| `dd` | 2-digit day | `10` |
| `HH` | 24-hour hour | `11` |
| `mm` | Minutes | `15` |
| `ss` | Seconds | `16` |

### One-arg vs two-arg form
```
// One arg: auto-parse input, format with given pattern
$.date.dateFormat("dd/MM/yyyy")

// Two args: explicit input format, then output format
$.date.dateFormat("yyyy-MM-ddTHH:mm:ss.fffffffZ", "dd MMM yyyy")
```

### Common patterns
```
$.date.dateFormat("yyyy-MM-dd")                → "2025-10-10"
$.date.dateFormat("dd/MM/yyyy HH:mm")          → "10/10/2025 11:15"
$.date.dateFormat("MMMM dd, yyyy")             → "October 10, 2025"
$.date.dateFormat("yyyyMMdd")                  → "20251010"
```
