# 41 — Try Date Format

## Expression
```
$.createdAt.tryDateFormat("yyyy-MM-ddTHH:mm:ss.fffZ", "MMMM dd, yyyy")
```

## Traditional JSONPath equivalent
```
$.createdAt.trydateformat("yyyy-MM-ddTHH:mm:ss.fffZ","MMMM dd, yyyy")
```

## Explanation
- `$.createdAt` → `"2010-09-05T09:33:18.111Z"`
- `.tryDateFormat(inputFormat, outputFormat)` — parses the date using the explicit input format, then formats with the output format
- First arg `"yyyy-MM-ddTHH:mm:ss.fffZ"` — how to parse the input (ISO 8601 with milliseconds)
- Second arg `"MMMM dd, yyyy"` — how to format the output

Result: `"September 05, 2010"`

### tryDateFormat vs dateFormat
Both functions are identical in Elwood. In traditional JSONPath, the difference was:
- `.dateFormat()` — throws an exception if parsing fails (error string in output)
- `.tryDateFormat()` — returns empty string `""` if parsing fails (silent failure)

In Elwood, both use safe parsing (`TryParse`/`TryParseExact`) and return the original value if parsing fails — no exceptions either way. The `tryDateFormat` name is kept for traditional JSONPath compatibility.

### Two-arg form
The two-argument form is essential when the input date format is non-standard:
```
// Standard ISO — auto-parse works, one arg is enough
$.isoDate.dateFormat("MMMM dd, yyyy")

// Non-standard format — must specify input format
$.weirdDate.tryDateFormat("dd/MMM/yyyy HH:mm", "yyyy-MM-dd")
```
