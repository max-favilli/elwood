# 33 — URL Encode

## Expression
```
{
  encoded: $.product.urlEncode(),
  roundtrip: $.product.urlEncode().urlDecode(),
  url: `https://example.com/{$.path.urlEncode()}`
}
```

## Explanation
- `$.product.urlEncode()` → encodes special characters to `%XX` sequences:
  - spaces → `%20`
  - `°` → `%C2%B0` (UTF-8)
- `.urlEncode().urlDecode()` → round-trip: encode then decode gives back the original string
- `` `https://example.com/{$.path.urlEncode()}` `` → string interpolation with URL encoding, useful for building safe URLs from dynamic values

### urlEncode vs urlDecode
| Method | Direction | Example |
|---|---|---|
| `.urlEncode()` | plain → encoded | `"40°C"` → `"40%C2%B0C"` |
| `.urlDecode()` | encoded → plain | `"40%C2%B0C"` → `"40°C"` |

### Use cases
- Building API URLs with dynamic query parameters
- Encoding filenames for cloud storage paths
- Decoding URL-encoded payloads from web hooks
