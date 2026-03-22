# 75 — fromText

## Expression
```
$.log.fromText() | where l => l.contains("ERROR")
```

## Explanation
`fromText()` splits a text string into an array of lines. Each line becomes a string element.

Then standard pipe operators work on the lines — here, `where` filters for lines containing "ERROR".

### Options
```
$.data.fromText({ delimiter: ";" })   // split by semicolons instead of newlines
```
