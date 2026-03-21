# 39 — DateTime Functions

## Expression
```
{
  formatted: $.event.timestamp.dateFormat("yyyy MMMM dd HH:mm:ss"),
  dateOnly: $.event.timestamp.dateFormat("yyyy-MM-dd"),
  unix: $.event.timestamp.toUnixTimeSeconds(),
  plusOneHour: $.event.timestamp.add("01:00:00").dateFormat("HH:mm:ss")
}
```

## Traditional JSONPath equivalents
```
$.event.timestamp.dateFormat('yyyy MMMM dd HH:mm:ss')
$.event.timestamp.dateFormat('yyyy-MM-dd')
$.event.timestamp.toUnixTimeSeconds()
$.event.timestamp.add(01:00:00).dateFormat('HH:mm:ss')
```

## Explanation
Elwood provides a full set of datetime functions:

### dateFormat(outputFormat)
Parses the date string and formats it:
```
$.timestamp.dateFormat("yyyy MMMM dd")    → "2026 March 21"
$.timestamp.dateFormat("dd/MM/yyyy")      → "21/03/2026"
```

### dateFormat(inputFormat, outputFormat)
Explicit input format for non-standard date strings:
```
$.weirdDate.dateFormat("dd-MMM-yyyy", "yyyy-MM-dd")
```

### add(timespan)
Adds a duration to a date (see test 37 for details):
```
$.timestamp.add("01:00:00")    → adds 1 hour
```

### toUnixTimeSeconds()
Converts to Unix epoch seconds (as string):
```
$.timestamp.toUnixTimeSeconds()    → "1774003626"
```

### now(format) and now(format, timezone)
Returns current time (non-deterministic — not tested here):
```
now("yyyy MMMM dd")                      → "2026 March 21" (UTC)
now("yyyy MMMM dd HH:mm:ss", "Europe/Rome")  → "2026 March 21 01:47:06" (Rome timezone)
```

### utcNow(format)
Explicitly UTC (same as `now` with no timezone):
```
utcNow("yyyy-MM-dd")    → "2026-03-21"
```

### Chaining
DateTime methods chain naturally:
```
$.timestamp.add("1.00:00:00").dateFormat("dd MMM yyyy")    → add a day, then format
```

### Traditional JSONPath comparison
| traditional JSONPath | Elwood |
|---|---|
| `.now('format')` | `now("format")` |
| `.now('format', 'Europe/Rome')` | `now("format", "Europe/Rome")` |
| `.utcnow('format')` | `utcNow("format")` |
| `.toUnixTimeSeconds()` | `.toUnixTimeSeconds()` |
| `.dateFormat('input', 'output')` | `.dateFormat("input", "output")` |
| `.add(01:00:00)` | `.add("01:00:00")` |
