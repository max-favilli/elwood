# 24 — First / Last with Predicate

## Expression
```
{
  firstAdult: $.users[*] | first u => u.age >= 18,
  lastActive: $.users[*] | last u => u.active,
  firstAny: $.users[*] | first
}
```

## Explanation
- `| first u => u.age >= 18` — returns the **first item** where `age >= 18` (Alice, full object)
- `| last u => u.active` — returns the **last item** where `active` is true (Eve, full object)
- `| first` — returns the **first item** with no filter (Alice, full object)

Note: `first` and `last` return a **single item**, not an array. You cannot pipe their result into `| select` (which expects an array). Use member access instead if you need a specific property.

`first` and `last` accept an **optional predicate** — a lambda that filters before selecting. This is equivalent to `| where pred | first` but more concise.

### Three forms
```
$.items[*] | first                    // first element
$.items[*] | first x => x.active     // first matching element (pipe operator)
someArray.first()                     // first element (method call syntax)
```

The pipe operator form (`| first pred`) supports predicates. The method call form (`.first()`) is for simple cases in method chains like `r.name.first.toCharArray().first()`.
