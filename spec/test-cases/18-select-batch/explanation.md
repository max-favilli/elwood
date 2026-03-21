# 18 — Select + Batch

## Expression
```
$[*] | select u => u.email | batch 4
```

## Traditional JSONPath equivalent
```
$[*].email.batch(4)
```

## Explanation
- `$[*]` — all items in the root array
- `| select u => u.email` — extract just the `email` from each item
- `| batch 4` — split the email list into chunks of 4

In traditional JSONPath, `$[*].email` implicitly selects the email property from each item (like a map/projection). In Elwood, this is explicit with `| select u => u.email` — the intent is clearer, especially in complex expressions.

Result: 3 batches — two with 4 emails each, one with 2.
