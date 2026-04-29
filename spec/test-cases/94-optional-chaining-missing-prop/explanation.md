# 94 — Optional chaining on missing properties

Verifies that `?.` returns null when the property doesn't exist on a non-null object (not just when the target itself is null).

1. **`$?.missing_field`** — `?.` on root with nonexistent property → `null`
2. **`$.items?.nonexistent`** — `?.` on array with nonexistent property on all items → `[]`
3. **`$?.default_address?.street`** — chained `?.` starting from root, first property missing → `null` (short-circuits)
