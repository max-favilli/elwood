# 74 — toCsv

## Expression
```
$.users[*] | select u => { name: u.name, age: u.age } | toCsv()
```

## Explanation
`toCsv()` converts an array of objects into a CSV string. Property names become headers (first row), values become data rows.

### Options
```
toCsv({ delimiter: ";", headers: false })
```
- `delimiter` — field separator (default `,`)
- `headers` — include header row (default `true`)
