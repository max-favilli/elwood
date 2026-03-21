# 57 — Left / Right

## Expression
```
{
  prefix: $.sku.left(2),
  first10: $.sku.left(10),
  suffix: $.sku.right(2),
  last5: $.sku.right(5),
  firstChar: $.code.left(),
  lastChar: $.code.right()
}
```

## Traditional JSONPath equivalent
```
$.sku.left(2)     → "JW"
$.sku.right(2)    → "XL"
```

## Explanation
- `.left(n)` — returns the first `n` characters of a string
- `.right(n)` — returns the last `n` characters
- No argument defaults to 1 character

### Input: `"JW-JACKET-001-BLK-XL"`
| Method | Result |
|---|---|
| `.left(2)` | `"JW"` |
| `.left(10)` | `"JW-JACKET-"` |
| `.right(2)` | `"XL"` |
| `.right(5)` | `"K-XL"` |

### Safe handling
If `n` exceeds the string length, the full string is returned (no error):
```
"abc".left(10)    → "abc"
"abc".right(10)   → "abc"
```

### left/right vs substring
| Need | Method |
|---|---|
| First N chars | `.left(n)` |
| Last N chars | `.right(n)` |
| Middle portion | `.substring(start, length)` |
| First char | `.left()` or `.toCharArray().first()` |

### Also available: padLeft / padRight
```
"42".padLeft(5, "0")     → "00042"
"hi".padRight(10, ".")   → "hi........"
```
