# 23 — First Character with toCharArray

## Expression
```
$.result[*] | select r => r.name.first.toCharArray().first()
```

## Traditional JSONPath equivalent
```
$.result[*].name['first'].tochararray().select($[*].first())
```

## Explanation
- `$.result[*]` — all items in the result array
- `| select r =>` — transform each item
- `r.name.first` — navigate to the `first` **property** of the `name` object (e.g. `"Christa"`)
- `.toCharArray()` — convert the string to an array of single characters: `["C", "h", "r", "i", "s", "t", "a"]`
- `.first()` — get the first element of the array: `"C"`

Result: `["C", "S", "K", "B", "D"]`

### `.first` (property) vs `.first()` (method)
This expression demonstrates an important Elwood distinction:
- `r.name.first` — **no parentheses** → property access (reads the `first` key from the `name` object)
- `.first()` — **with parentheses** → method call (gets the first element of an array)

The parentheses `()` always disambiguate. If it looks like a function call, it's a function call.

### Alternative approach
You could also use `.substring(0, 1)` for this specific case:
```
$.result[*] | select r => r.name.first.substring(0, 1)
```
But `toCharArray()` is useful whenever you need to work with individual characters — splitting, filtering, or transforming them.
