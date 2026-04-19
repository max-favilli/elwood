# GroupBy with memo function and bracket property access

Combines several features in a real-world product image grouping scenario:

1. **Bracket property access** — `item["label-en_US"]` accesses a property whose name contains a hyphen, which isn't valid with dot notation
2. **Memoized function** — `memo label => ...` caches the colorway extraction so repeated calls with the same label value are not recomputed
3. **String split + interpolation** — splits `"A65576_E0272_9-f800-..."` on `_`, then recombines the first two parts via `` `{...}_{...}` `` to produce `"A65576_E0272"`
4. **groupBy with computed key** — groups items by the extracted colorway, producing `.key` and `.items` per group
5. **Nested select** — inside the outer `select`, a nested `g.items | select i => i.external_url` collects all URLs for each colorway group
