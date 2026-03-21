# 59 — Case & Trim (Extended)

## Expression
```
{
  upper: $.name.toUpper(),
  upperFirst: $.name.toUpper(1),
  lower: $.name.toUpper().toLower(1),
  trimHash: $.padded.trim("#"),
  trimLeadingZeros: $.code.trimStart("0"),
  trimTrailingDots: $.file.trimEnd(".")
}
```

## Traditional JSONPath equivalents
```
$.name.toUpper()
$.name.toUpper(1)
$.padded.trim('#')
$.code.trimStart('0')
$.file.trimEnd('.')
```

## Explanation

### toLower / toUpper — full and positional

**No args — entire string:**
```
"hello".toUpper()     → "HELLO"
"HELLO".toLower()     → "hello"
```

**With position (1-based) — single character:**
```
"hello".toUpper(1)    → "Hello"    // uppercase first char only
"HELLO".toLower(1)    → "hELLO"    // lowercase first char only
```

Positional case change is useful for capitalizing names or normalizing case at specific positions without affecting the rest of the string.

### trim / trimStart / trimEnd — with characters

**No args — whitespace:**
```
"  hello  ".trim()           → "hello"
"  hello  ".trimStart()      → "hello  "
"  hello  ".trimEnd()        → "  hello"
```

**With chars — trim specific characters:**
```
"###data###".trim("#")       → "data"
"000042".trimStart("0")      → "42"
"file.txt...".trimEnd(".")   → "file.txt"
```

The argument is a string of characters to trim — each character in the string is trimmed individually (not the whole string as a unit):
```
"xxyxdata".trimStart("xy")   → "data"    // trims any x or y from start
```
