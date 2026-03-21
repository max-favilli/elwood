# 63 — Reduce

## Expression
```
{
  sum: $.numbers[*] | reduce (acc, x) => acc + x,
  sentence: $.words[*] | reduce (acc, w) => acc + " " + w,
  totalValue: $.orders[*] | reduce (acc, o) => acc + o.qty * o.price from 0
}
```

## Explanation
`reduce` folds an array into a single value by applying an accumulator function to each element.

### Syntax
```
| reduce (accumulator, item) => expression
| reduce (accumulator, item) => expression from initialValue
```

- `accumulator` — the running result, updated on each iteration
- `item` — the current array element
- `from initialValue` — optional starting value for the accumulator

### Three examples

**1. Sum numbers (no initial value):**
```
[1, 2, 3, 4, 5] | reduce (acc, x) => acc + x
```
- Start: `acc = 1` (first element)
- Step 1: `1 + 2 = 3`
- Step 2: `3 + 3 = 6`
- Step 3: `6 + 4 = 10`
- Step 4: `10 + 5 = 15`

**2. Build a sentence:**
```
["Hello", "World", "from", "Elwood"] | reduce (acc, w) => acc + " " + w
```
→ `"Hello World from Elwood"`

**3. Compute total value (with initial value):**
```
$.orders[*] | reduce (acc, o) => acc + o.qty * o.price from 0
```
- `from 0` — start with 0 (since the first element is an object, not a number)
- Each step: add `qty * price` to the accumulator
- Result: `2*199.99 + 1*149.50 + 3*29.99 = 639.45`

### When to use reduce vs built-in aggregations
| Need | Use |
|---|---|
| Sum numbers | `\| sum` (simpler) |
| Count items | `\| count` (simpler) |
| Min/max | `\| min` / `\| max` (simpler) |
| Custom accumulation | `\| reduce` (general purpose) |
| Build string from array | `\| reduce` or `\| concat` |
| Compute complex value | `\| reduce` (only option) |

`reduce` is the **general-purpose** fold — `sum`, `min`, `max`, `count` are all special cases of it. Use the specific operators when they fit; use `reduce` for everything else.

### JSONata equivalent
```
$reduce(numbers, function($acc, $x) { $acc + $x })
```
