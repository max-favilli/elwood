# 32 — URL Decode + Split + Take

## Expression
```
$.foo.urlDecode().split(".") | take 1
```

## Traditional JSONPath equivalent
```
$.foo.ToUrlDecoded().Split('.').Take(1)
```

## Explanation
- `$.foo` → `"A660014%20-%20Color%2040%C2%B0C.svg"`
- `.urlDecode()` → URL-decode the string: `"A660014 - Color 40°C.svg"`
  - `%20` → space
  - `%C2%B0` → `°` (degree symbol, UTF-8 encoded)
- `.split(".")` → split by `.`: `["A660014 - Color 40°C", "svg"]`
- `| take 1` → take the first element: `["A660014 - Color 40°C"]`

Result: `["A660014 - Color 40°C"]` — the filename without the extension.

### Method chaining into pipes
This expression shows how **method chains** (`.urlDecode().split(".")`) flow naturally into **pipe operators** (`| take 1`). Methods transform individual values; pipes operate on collections. You can freely mix them in one expression.

### Available URL methods
```
$.encoded.urlDecode()     → decode %XX sequences
$.plain.urlEncode()       → encode special characters to %XX
```

### Traditional JSONPath naming difference
| traditional JSONPath | Elwood |
|---|---|
| `.ToUrlDecoded()` | `.urlDecode()` |
| `.Split('.')` | `.split(".")` |
| `.Take(1)` | `\| take 1` |

Elwood uses camelCase for methods and pipe operators for collection operations.
