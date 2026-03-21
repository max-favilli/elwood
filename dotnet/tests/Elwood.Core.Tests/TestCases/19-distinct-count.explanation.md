# 19 — Distinct + Count

## Expression
```
$[*] | select u => u.company | distinct | count
```

## Traditional JSONPath equivalent
```
$[*].selectmany($$$[*].company.distinct().cache("foo",true)).distinct().count()
```

## Explanation
- `$[*]` — all items
- `| select u => u.company` — extract the company name from each item
- `| distinct` — remove duplicates
- `| count` — count the unique values

**Why the Elwood version is simpler:** In traditional JSONPath, the same operation required `selectmany` with parent navigation (`$$$`) to reach the root array from inside the loop, plus `.cache()` to avoid re-evaluating the expression on every iteration. In Elwood, the pipe flows naturally top-to-bottom — no parent navigation, no caching, no `selectmany` workaround.
