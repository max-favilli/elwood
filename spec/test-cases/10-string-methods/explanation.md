# 10 — String Methods

## Expression
```
$.users[*] | select u => u.name.toLower().replace(" ", "_")
```

## Explanation
- `u.name` — get the name string (e.g. `"Alice Smith"`)
- `.toLower()` — convert to lowercase → `"alice smith"`
- `.replace(" ", "_")` — replace spaces with underscores → `"alice_smith"`

Methods are **chained with dots** on any value. Elwood provides built-in string methods: `toLower`, `toUpper`, `trim`, `trimStart`, `trimEnd`, `contains`, `startsWith`, `endsWith`, `replace`, `substring`, `split`, `length`.
