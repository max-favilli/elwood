# 35 — Hash

## Expression
```
$.products[*] | select p => {
  sku: p.sku,
  fullHash: p.sku.hash(),
  shortHash: p.sku.hash(8),
  idempotent: p.sku.hash(8) == p.sku.hash(8)
}
```

## Traditional JSONPath equivalent
```
$.products[*].select($.toobject({
  sku: $.sku,
  fullHash: $.sku.hash(),
  shortHash: $.sku.hash(8)
}))
```

## Explanation
- `.hash()` — computes an MD5 hash of the string, returned as lowercase hex (32 characters)
- `.hash(8)` — same hash, truncated to 8 characters (a "short hash")
- `p.sku.hash(8) == p.sku.hash(8)` — hashing is **deterministic**: same input always produces the same output

### How it works
1. Input string is UTF-8 encoded
2. MD5 hash is computed (16 bytes)
3. Bytes are converted to lowercase hexadecimal (32 chars)
4. Truncated to the requested length (default: 32 = full hash)

### hash(length)
| Call | Result for `"JW-JACKET-001"` |
|---|---|
| `.hash()` | `"55d35676b67575c63004fa14a875e70e"` (32 chars) |
| `.hash(8)` | `"55d35676"` (8 chars) |
| `.hash(16)` | `"55d35676b67575c6"` (16 chars) |

### Common use cases
- **Deduplication**: hash output content to detect unchanged data (`hashSettings.store` in root configs)
- **Cache keys**: generate deterministic keys from dynamic values
- **ID generation**: create short unique identifiers from composite keys (e.g. `sku + season`)

### Example: composite hash
```
let compositeKey = `{p.sku}-{p.season}`
compositeKey.hash(12)
```
