# 113 — In (Membership) tested once per row

Verifies `.in(list)` when the same list is tested against every row of another collection —
the usual shape of "keep the rows whose value is in this list".

```
let allowed = $.allowed
let activeCodes = $.codes[*] | where c => c.active | select c => c.code

return {
  mixedKinds: $.rows[*] | where r => r.v.in(allowed) | select r => r.id,
  fromPipeline: $.rows[*] | where r => r.v.in(activeCodes) | select r => r.id,
  notIn: $.rows[*] | where r => !r.v.in(activeCodes) | count
}
```

A value is in the list when it equals one of the elements, by the same rules as `==`:

- kinds must match — the string `"7"` is not the number `7`, and `"true"` is not `true`
- strings are case-sensitive — `"A1"` and `"a1"` are different entries
- numbers match within a tiny tolerance — `7.00000000001` equals `7`
- `null` is in a list that contains `null`
- arrays and objects match by content — `[1, 2]` matches, `[2, 1]` does not

Bind the list with `let` and test against it with `.in()`. This is the idiom for membership:
the engines index a list that is tested repeatedly, so the cost stays proportional to the
number of rows plus the size of the list. Writing the same test as
`list | any x => x == r.v` scans the whole list for every row.
