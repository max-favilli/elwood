# 73 — fromCsv

## Expression
```
$.data.fromCsv() | select r => { name: r.name, age: r.age.toNumber() }
```

## Explanation
`fromCsv()` parses a CSV string into an array of objects. The first row is treated as headers (property names), each subsequent row becomes an object.

Input CSV:
```
name,age,city
Alice,30,Berlin
Bob,25,Munich
Charlie,35,Hamburg
```

After `fromCsv()`: `[{ name: "Alice", age: "30", city: "Berlin" }, ...]`

Note: all CSV values are strings. Use `.toNumber()` to convert numeric fields.

### Options
```
$.data.fromCsv({ delimiter: ";", headers: true, quote: "'" })
```
- `delimiter` — field separator (default `,`)
- `headers` — first row is headers (default `true`). If `false`, returns array of arrays.
- `quote` — quote character for escaping (default `"`)
