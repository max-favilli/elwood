# 28 — Select: traditional JSONPath vs Elwood

This test demonstrates how Elwood's `| select` with lambdas replaces Traditional JSONPath's `.select()` with implicit `$` context.

## Elwood expression
```
$.result[*] | select r => r.name.first.toCharArray().first()
```

## Traditional JSONPath equivalent
```
$.result[*].name['first'].tochararray().select($[*].first())
```

## Step-by-step comparison

### traditional JSONPath flow
```
$.result[*]               → 5 result objects (implicit pipeline, one at a time)
  .name                   → auto-maps: extracts the name object from each
  ['first']               → bracket notation: gets the "first" property from each name
  .tochararray()          → converts each name string to a char array
  .select(                → for each char array:
    $[*]                  →   $ = current char array, [*] = all chars
    .first()              →   take the first character
  )
```

Key traditional JSONPath patterns needed:
- **`['first']`** — bracket notation because `first` could be confused with a function
- **`.select($[*].first())`** — `$` re-enters the current item in the pipeline
- **`$[*]`** — wildcard to "unwrap" the current array item
- Implicit auto-mapping through the entire chain

### Elwood flow
```
$.result[*]               → 5 result objects (array)
| select r =>             → for each item (named "r"):
    r.name.first          →   navigate: r → name object → "first" property (e.g. "Christa")
    .toCharArray()        →   convert to char array: ["C","h","r","i","s","t","a"]
    .first()              →   get first element: "C"
```

Key Elwood advantages:
- **`r.name.first`** — `first` is unambiguously a property (no `()` = property, with `()` = method)
- **`r =>`** — named lambda, no confusion about what `$` refers to
- **`.first()`** — method call directly on the char array, no need for `$[*]` re-entry
- **No auto-mapping chain** — explicit `| select` makes the iteration visible

### The core difference
In traditional JSONPath, `.select()` uses **implicit context** — `$` inside select refers to "the current item in the pipeline." You need `$[*]` to expand arrays and `$$`/`$$$` to navigate to parents.

In Elwood, `| select r =>` uses **named context** — `r` is the current item. No ambiguity, no parent navigation counting, no bracket notation workarounds.

## Result
```json
["C", "S", "K", "B", "D"]
```
The first character of each person's first name.
