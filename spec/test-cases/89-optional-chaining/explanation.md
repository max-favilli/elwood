# Optional chaining (`?.`) with omitNulls

Demonstrates safe property access on nullable objects using `?.`, combined with `.omitNulls()` to strip null-valued properties.

1. **`$.variant?.sku`** — returns the SKU when variant exists, null when variant is null (no error)
2. **`.omitNulls()`** — removes the null sku/title properties from Order 2's output
3. **Real-world pattern** — Shopify orders where some line items have `variant: null`
4. **Contrast with strict access** — `$.variant.sku` (without `?`) would throw an enriched error identifying the null variant and suggesting `?.`
