# 76 — toText

## Expression
```
$.items[*] | select i => `{i.id}: {i.name}` | toText()
```

## Explanation
`toText()` joins an array of strings into a single text string with newline separators.

### Options
```
toText({ delimiter: ", " })    // join with commas instead of newlines
```
