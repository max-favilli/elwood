# 20 — Memoized Functions

## Script
```
let benefitsFor = memo isFullTime => $.benefits[*] | where b => b.fullTimeOnly == isFullTime | select b => b.name

return $.employees[*] | select e => {
  name: e.name,
  benefits: benefitsFor(e.fullTime)
}
```

## Traditional JSONPath equivalent pattern
```
$.employees[*].select($.set({_ft:$.fullTime}).toobject({
  name: $.name,
  benefits: $$$$$.benefits[*].where($.fullTimeOnly = $.get($._ft)).select($.name).cache("{$.get($._ft)}")
}))
```

## Explanation
- `memo isFullTime => ...` — defines a **memoized function**. The function body is evaluated once per distinct argument value and the result is cached automatically
- `$.benefits[*] | where b => b.fullTimeOnly == isFullTime | select b => b.name` — the function body: filter benefits by the `isFullTime` flag, return their names
- `benefitsFor(e.fullTime)` — call the function for each employee

**How memoization works:**
1. First call with `true` (Alice) → evaluates the body, caches result under key `true`
2. Second call with `false` (Bob) → evaluates the body, caches result under key `false`
3. Third call with `true` (Charlie) → **cache hit**, returns instantly
4. Fourth call with `false` (Diana) → **cache hit**, returns instantly

With 100,000 employees and a boolean flag, the function body executes **2 times** instead of 100,000.

**Why this replaces Traditional JSONPath's `.cache()`:**
- traditional JSONPath: `.cache("{$.get($._ft)}")` — manual string-interpolated cache key, `set`/`get` for variable scoping, `$$$$$` for parent navigation
- Elwood: `memo flag => expr` — automatic cache key from arguments, `let` for scoping, `$` paths resolve from where `memo` was defined

Multi-parameter memoization also works: `memo (country, tier) => ...` — the cache key is the combination of all arguments.
