# 23 — First/Last Character of Strings

## Script
```
let names = $.result[*] | select r => r.name.first
return {
  viaCharArray: names | select n => n.toCharArray().first(),
  firstDirect: names | select n => n.first(),
  lastDirect: names | select n => n.last()
}
```

## Explanation
Three ways to extract characters from strings:

1. **`n.toCharArray().first()`** — explicit: convert to character array, then take first element
2. **`n.first()`** — direct: `.first()` on a string returns its first character
3. **`n.last()`** — direct: `.last()` on a string returns its last character

All three produce the same type of result (single-character strings).

The names from the input are: `Christa`, `Serenity`, `Kim`, `Bulah`, `Dannie`.
- First characters: `C`, `S`, `K`, `B`, `D`
- Last characters: `a`, `y`, `m`, `h`, `e`

### `.first` (property) vs `.first()` (method)
This test also demonstrates an important Elwood distinction:
- `r.name.first` — **no parentheses** → property access (reads the `first` key from the `name` object)
- `n.first()` — **with parentheses** → method call (gets the first character of a string, or first element of an array)