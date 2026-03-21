# Elwood Syntax Reference

## Overview

Elwood is a functional JSON transformation DSL combining JSONPath navigation, KQL-style pipes, and LINQ-style lambda expressions.

---

## Navigation (JSONPath)

```
$                         Root of the input document
$.field                   Property access
$.nested.field            Nested property access
$[0]                      Array index
$[*]                      All array elements (wildcard)
$[2:5]                    Array slice (elements 2,3,4)
$[:3]                     First 3 elements
$[5:]                     From index 5 to end
$[-2:]                    Last 2 elements
$..field                  Recursive descent (find in all descendants)
```

Property access auto-maps over arrays: `$.items[*].name` extracts `name` from each item.

## Literals

```
"hello"  or  'hello'      String (double or single quotes)
42                         Integer
3.14                       Decimal
true / false               Boolean
null                       Null
[1, 2, 3]                 Array
{ key: value, k2: v2 }    Object
```

## String Interpolation

Use backticks with `{expression}` placeholders:

```
`{$.firstName} {$.lastName} is {$.age} years old`
```

String concatenation with `+` for strings containing `{}`:

```
"query { items(first: " + $.count.toString() + ") }"
```

## Spread Operator

Copy all properties from an object into a new object:

```
{ ...original, newProp: "value" }
{ ...base, ...overrides, computed: expr }
```

## Computed Property Keys

Use `[expression]` for dynamic property names (like JavaScript):

```
{ [$.fieldName]: $.fieldValue }
{ [`prefix_{$.id}`]: $.data }
{ ...obj, [dynamicKey]: newValue }
```

## Pipes

Pipes transform data left-to-right, KQL-style:

```
expression | operation1 | operation2 | ...
```

### Collection Operations

| Operator | Syntax | Description |
|---|---|---|
| `where` | `\| where predicate` | Filter items |
| `select` | `\| select projection` | Transform each item |
| `selectMany` | `\| selectMany projection` | Flatten nested results |
| `orderBy` | `\| orderBy key [asc\|desc]` | Sort (multi-key with commas) |
| `groupBy` | `\| groupBy key` | Group → objects with `.key` and `.items` |
| `distinct` | `\| distinct` | Remove duplicates |
| `take` | `\| take n` | First n items |
| `skip` | `\| skip n` | Skip n items |
| `batch` | `\| batch n` | Chunk into groups of n |
| `join` | `\| join source on lKey equals rKey [into alias]` | Join two collections |
| `concat` | `\| concat` or `\| concat separator` | Join array into string |
| `index` | `\| index` | Replace items with 0-based indices |
| `reduce` | `\| reduce (acc, x) => expr [from init]` | General-purpose fold |

### Aggregation

| Operator | Description |
|---|---|
| `\| count` | Number of items |
| `\| sum` | Sum of numeric values |
| `\| min` | Minimum value |
| `\| max` | Maximum value |
| `\| first` | First item (optional predicate: `\| first x => x.active`) |
| `\| last` | Last item (optional predicate: `\| last x => x.active`) |

### Quantifiers

| Operator | Syntax | Description |
|---|---|---|
| `any` | `\| any predicate` | True if any item matches |
| `all` | `\| all predicate` | True if all items match |

### Pattern Matching

```
expression | match
  "value1" => result1,
  "value2" => result2,
  _ => defaultResult
```

## Lambda Expressions

Named parameter bindings — eliminates the `$$$$$` parent navigation problem:

```
// Single parameter
u => u.name.toLower()

// Multi parameter
(x, y) => x.value + y.value

// In reduce
(acc, item) => acc + item.price
```

### Implicit $ context

In pipe operations, `$` refers to the current item when no lambda is used:

```
$.users[*] | where $.active | select $.name
// equivalent to:
$.users[*] | where u => u.active | select u => u.name
```

## Let Bindings

Script-scoped immutable variables:

```
let adults = $.users[*] | where u => u.age >= 18
let count = adults | count
return { adults: adults, count: count }
```

Variables are visible to all subsequent bindings and the return expression.

## Memoized Functions

Cache expensive computations by argument value:

```
let findByFlag = memo flag => $.items[*] | where i => i.active == flag
$.data[*] | select d => findByFlag(d.active)
```

The function body executes once per distinct argument value. Subsequent calls with the same argument return the cached result.

## Conditionals

```
if condition then expression else expression
```

Example:
```
$.users[*] | select u => if u.age >= 18 then "adult" else "minor"
```

## Operators

### Arithmetic
`+`, `-`, `*`, `/` with standard precedence. Parentheses for grouping.

`+` also concatenates strings: `"hello" + " " + "world"`

### Comparison
`==`, `!=`, `<`, `<=`, `>`, `>=`

Comparisons work on both numbers and strings (ordinal comparison for strings).

### Logical
`&&` (and), `||` (or), `!` (not)

## Built-in Methods

Methods are called with dot notation on values. Most also work as pipe operators.

### String Methods

| Method | Description |
|---|---|
| `.toLower()` | Lowercase entire string |
| `.toLower(n)` | Lowercase character at position n (1-based) |
| `.toUpper()` | Uppercase entire string |
| `.toUpper(n)` | Uppercase character at position n (1-based) |
| `.trim()` | Trim whitespace |
| `.trim(chars)` | Trim specific characters |
| `.trimStart()` / `.trimStart(chars)` | Trim leading whitespace/characters |
| `.trimEnd()` / `.trimEnd(chars)` | Trim trailing whitespace/characters |
| `.left(n)` | First n characters (default 1) |
| `.right(n)` | Last n characters (default 1) |
| `.padLeft(width, char)` | Pad from left to width |
| `.padRight(width, char)` | Pad from right to width |
| `.contains(str)` | Contains substring (case-insensitive) |
| `.startsWith(str)` | Starts with prefix (case-insensitive) |
| `.endsWith(str)` | Ends with suffix (case-insensitive) |
| `.replace(search, repl)` | Replace occurrences |
| `.replace(search, repl, true)` | Replace case-insensitive |
| `.substring(start, length?)` | Extract substring |
| `.split(delimiter)` | Split into array |
| `.length()` | String length (or array length) |
| `.toCharArray()` | Convert string to array of characters |
| `.regex(pattern)` | Extract all regex matches as array |
| `.urlDecode()` | URL-decode `%XX` sequences |
| `.urlEncode()` | URL-encode special characters |
| `.sanitize()` | Transliterate special chars to ASCII (ß→ss, accents removed, Greek→Latin) |
| `.concat(sep?, ...arrays)` | Join with separator, optionally merging additional arrays |

### Numeric Methods

| Method | Description |
|---|---|
| `.round()` | Round to integer (away from zero) |
| `.round(decimals)` | Round to N decimals |
| `.round("toEven")` | Banker's rounding |
| `.round(decimals, "toEven")` | Decimals + rounding mode |
| `.floor()` | Round down |
| `.ceiling()` | Round up |
| `.truncate()` | Remove decimal part (toward zero) |
| `.abs()` | Absolute value |

### Membership

| Method | Description |
|---|---|
| `.in(array)` | Check if value exists in array |
| `.in(arr1, arr2, "val")` | Check against union of multiple sources |

### Null/Empty Checks

| Method | No arg (→ boolean) | With arg (→ fallback) |
|---|---|---|
| `.isNull()` | True if null | Return fallback if null |
| `.isEmpty()` | True if null, `""`, or `[]` | Return fallback if empty |
| `.isNullOrEmpty()` | Same as isEmpty | Return fallback if empty |
| `.isNullOrWhiteSpace()` | Also true for `"   "` | Return fallback if whitespace |

### Type Conversion

| Method | Description |
|---|---|
| `.toString()` | Convert to string |
| `.toString(format)` | Format string for numbers/dates (e.g. `"F2"`, `"yyyy-MM-dd"`) |
| `.toNumber()` | Parse to number |
| `.convertTo("Int32")` | Convert to int (truncates decimals) |
| `.convertTo("Double")` | Convert to double |
| `.convertTo("Boolean")` | Convert to boolean |
| `.boolean()` | Coerce to boolean (truthiness) |
| `.not()` | Negate truthiness |

### Collection Methods

| Method | Description |
|---|---|
| `.count()` | Number of items |
| `.length()` | Array length or string length |
| `.first()` | First element |
| `.last()` | Last element |
| `.sum()` | Sum of numeric values |
| `.min()` | Minimum value |
| `.max()` | Maximum value |
| `.index()` | Array of 0-based indices |
| `.take(n)` | First n items |
| `.skip(n)` | Skip n items |
| `.keep(prop1, prop2, ...)` | Keep only named properties |
| `.remove(prop1, prop2, ...)` | Remove named properties |
| `.clone()` | Deep copy |

### DateTime Methods

| Method | Description |
|---|---|
| `.dateFormat(outputFmt)` | Format date string |
| `.dateFormat(inputFmt, outputFmt)` | Parse with explicit format, then format |
| `.tryDateFormat(inputFmt, outputFmt)` | Same as dateFormat (safe parsing) |
| `.add(timespan)` | Add duration (e.g. `"1.06:30:00"`) |
| `.toUnixTimeSeconds()` | Convert to Unix epoch seconds |

### Utility Functions

| Function | Description |
|---|---|
| `now(format?)` | Current UTC time |
| `now(format, timezone)` | Current time in timezone |
| `utcNow(format?)` | Current UTC time (explicit) |
| `newGuid()` | Generate unique GUID |
| `range(start, count)` | Generate numeric sequence |
| `boolean(value)` | Coerce to boolean |

### Hashing & Crypto

| Method | Description |
|---|---|
| `.hash()` | MD5 hash (32 char hex) |
| `.hash(length)` | Truncated hash |
| `.rsaSign(data, privateKeyPem)` | RSA-SHA1 signature (legacy compatibility) |

---

## Scripts vs Expressions

**Expression**: A single Elwood expression. Used in `eval` mode or as values in YAML maps.

```
$.users[*] | where u => u.active | select u => u.name
```

**Script**: Multiple `let` bindings with a `return`. Used in `run` mode or `.elwood` files.

```
let active = $.users[*] | where u => u.active
let names = active | select u => u.name.toLower()
return { names: names, total: active | count }
```

---

## Error Reporting

Elwood provides rich diagnostics:

```
Error at line 1, col 23:
  Property 'vresion' not found on Object.
  Did you mean 'version'? Available: version, count
```

- Source location (line, column) for every error
- "Did you mean?" suggestions based on Levenshtein distance
- Available property names listed when a property is not found

---

## CLI Usage

```bash
# Interactive REPL
elwood

# Evaluate single expression
elwood eval "$.users[*] | where $.active" --input data.json

# Run script file
elwood run transform.elwood --input payload.json

# Pipe JSON via stdin
echo '{"x":1}' | elwood eval "$.x + 1"
```

### REPL Commands

| Command | Description |
|---|---|
| `:load <file>` | Load JSON input from file |
| `:json <json>` | Set inline JSON as input |
| `:input` | Show current input |
| `:script` | Toggle multi-line script mode |
| `:quit` | Exit |
