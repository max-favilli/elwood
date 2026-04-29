# 95 — Trailing `?` optional property marker

Verifies that `prop?` (trailing question mark after a property name) returns null when the property doesn't exist, as an alternative to `?.prop` (leading optional chaining on the target).

1. **`$.address.city`** — strict access on existing nested property → `"Berlin"`
2. **`$.default_address?`** — trailing `?` on missing root-level property → `null`
3. **`$.address.zip?`** — trailing `?` on missing nested property → `null`
4. **`$.address.country?`** — trailing `?` on another missing nested property → `null`
5. **`$.items[*].nonexistent?`** — trailing `?` on missing property in array auto-map → `[]`
