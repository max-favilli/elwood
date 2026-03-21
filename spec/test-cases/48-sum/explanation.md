# 48 — Sum

## Expression
```
$.details[*] | sum
```

## Traditional JSONPath equivalent
```
$.details[*].sum()
```

## Explanation
- `$.details[*]` → `[5.2, 1]`
- `| sum` → `6.2` (add all numeric values)

### Both forms work
```
$.details[*] | sum        // pipe operator
$.details[*].sum()        // method call (traditional style)
```

### Other numeric aggregations
```
$.values[*] | sum         → total of all values
$.values[*] | min         → smallest value
$.values[*] | max         → largest value
$.values[*] | count       → number of items
```

### Sum with projection
To sum a specific property from an array of objects:
```
$.orders[*] | select o => o.total | sum
// or combined:
$.orders[*] | select o => o.price * o.qty | sum
```
