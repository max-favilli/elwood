# 61 — Regex

## Expression
```
$.users[*] | select u => u.username.regex("[A-Za-z]+").concat(", ")
```

## Traditional JSONPath equivalent
```
$[*].username.Regex('(?:[(A-Z)|(a-z)][a-z]+|\\b[A-Z]\\S*|\\S+)').concat(', ')
```

## Explanation
- `u.username` → e.g. `"Christa-Klein"`
- `.regex("[A-Za-z]+")` → extract all regex matches: `["Christa", "Klein"]`
- `.concat(", ")` → join with comma: `"Christa, Klein"`

### How .regex() works
`.regex(pattern)` applies a regular expression to a string and returns **all matches** as an array:
```
"Hello World 123".regex("[A-Za-z]+")     → ["Hello", "World"]
"2026-03-21".regex("\\d+")              → ["2026", "03", "21"]
"abc@def.com".regex("[^@]+")            → ["abc", "def.com"]
```

### Common patterns
| Pattern | Matches |
|---|---|
| `"[A-Za-z]+"` | Words (letters only) |
| `"\\d+"` | Number sequences |
| `"[^@]+"` | Split by `@` |
| `"\\w+"` | Word characters (letters, digits, underscore) |
| `"[A-Z][a-z]+"` | Capitalized words |

### Chaining with other operations
```
// Extract words and take first
"Christa-Klein".regex("[A-Za-z]+").first()      → "Christa"

// Extract numbers and sum
"qty:5 price:10".regex("\\d+") | select v => v.toNumber() | sum    → 15

// Match and filter
"abc 123 def".regex("\\w+") | where v => v.toNumber() > 0    → ["123"]
```

### Traditional JSONPath comparison
Same function, same behavior. The regex pattern syntax is standard .NET regular expressions in both traditional JSONPath and Elwood.
