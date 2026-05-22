# 112 -- Array Spread

The spread operator `...` inside an array literal copies all elements from the
source array into the new array:

```
let a = $.first        // [1, 2, 3]
let b = $.second       // [4, 5]

[...a, ...b, 6, 7]    // [1, 2, 3, 4, 5, 6, 7]
```

Common patterns: `[...existing, newItem]`, `[0, ...rest]`, `[...a, ...b]`.
