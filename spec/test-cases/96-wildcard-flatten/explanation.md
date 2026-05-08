# 96 — [*] wildcard flatten

Verifies that `[*]` flattens one level, matching JSONPath nodeset semantics.

`$.styles[*].colorways` auto-maps over the styles array, producing an array of arrays:
`[[{code:"1010",...},{code:"2020",...}],[{code:"3030",...}]]`

The second `[*]` flattens this into a single flat array of colorway objects,
so downstream pipes like `| select c => c.code` can iterate individual items.

Without the flatten, `[*]` on an array-of-arrays would preserve nesting and
require `| selectMany c => c` to flatten explicitly.
