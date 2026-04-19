# omitNulls — strip null properties from objects

Demonstrates the `.omitNulls()` method for removing null-valued properties from objects, a common need when migrating from Eagle JSON maps (which have `nullValueHandling: "Ignore"` by default).

1. **Object construction** — builds a new object with explicit property mapping
2. **`.omitNulls()`** — removes any property whose value is `null`; properties with `false`, `0`, `""`, or `[]` are kept
3. **Auto-map over array** — when applied inside `select`, each element is processed independently
4. **Varying null positions** — Alice has null phone, Bob has null email+city, Charlie has no nulls — verifies each case
