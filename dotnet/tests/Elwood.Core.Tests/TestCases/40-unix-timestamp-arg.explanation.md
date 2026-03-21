# 40 — Unix Timestamp from Property

## Expression
```
$[*] | select f => {
  file: f.FileName,
  direct: f.LastWriteTimeUtc.toUnixTimeSeconds(),
  viaArg: f.toUnixTimeSeconds(f.LastWriteTimeUtc)
}
```

## Traditional JSONPath equivalent
```
$[0].LastWriteTimeUtc.toUnixTimeSeconds()
$[0].toUnixTimeSeconds($.LastWriteTimeUtc)
```

## Explanation
Both forms produce the same result:
- `f.LastWriteTimeUtc.toUnixTimeSeconds()` — call on the date value directly (**recommended**)
- `f.toUnixTimeSeconds(f.LastWriteTimeUtc)` — pass the date as an argument (traditional style)

The test verifies both produce identical output: `"1774054026"` and `"1774008000"`.

### traditional JSONPath vs Elwood pattern
In traditional JSONPath, `.toUnixTimeSeconds($.LastWriteTimeUtc)` passes the date as an **argument** — the function resolves the path and converts it. The target (`$[0]`) is just the context.

In Elwood, you can use either form, but calling the method directly on the date value is cleaner:
```
f.LastWriteTimeUtc.toUnixTimeSeconds()
```

### toUnixTimeSeconds() forms
```
// On a date string (recommended)
$.timestamp.toUnixTimeSeconds()              → epoch seconds from the value

// With date argument (traditional JSONPath compat)
$.toUnixTimeSeconds($.datefield)             → epoch seconds from the arg

// No target, no arg — current UTC
toUnixTimeSeconds()                           → current time as epoch
```

### Use cases
- Generating cache keys with time-based expiry
- Comparing dates numerically
- Building API parameters that require epoch timestamps
