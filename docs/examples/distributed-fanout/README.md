# Explicit fan-out sample

This M7.4 sample is available for local draft testing. Production deployment remains disabled until M7.5 supplies item inspection, recovery, retention and admission accounting. The URLs deliberately use reserved `.invalid` hosts; they are not public test services.

Create an async draft with `pipeline.elwood.yaml`, open **Test draft**, select **Mocks**, paste `input.json` and `mocks.json`, then run. The source makes two simulated product lookups; the output maps and delivers each result independently. Its aggregated output is:

```json
[{"productId":"A","name":"Product A"},{"productId":"B","name":"Product B"}]
```

For a real-system local test, replace both URLs with controlled endpoints and select **Real systems**. That action makes real calls. Local testing runs items sequentially with the same transformations, retry decisions and completed-destination receipts used by the distributed runner. It does not test cross-worker concurrency or offer durable acceptance.

Source `path` is evaluated once against the assembled dependencies. It must return an array; each element becomes `$slice`, and its zero-based position becomes `$index`. Source maps receive the fetched response as `$`, retain the original `$slice`, and can use the frozen stage `$idm`. Mapped source results are aggregated under the source name (`$idm.products` here), in input order. Arrays returned by an individual item stay nested; aggregation does not flatten them.

Output `fanOut.path` is a separate selector evaluated once against the final IDM. For each element, the existing output `path` (if supplied) and `map` run on that element. Without `fanOut`, output `path` still selects one value that is mapped and delivered once, including when that value is an array. Each expanded item delivers its destinations in their existing order. Results are aggregated in manifest order regardless of completion order.

Source `concurrency` and output `fanOut.concurrency` limit owners across the whole distributed stage, not per worker. Default is 1, maximum 1000. Source `maxItems` and output `fanOut.maxItems` default to 10000, maximum 100000; the entire selector is checked before dispatch. Empty arrays produce no calls and an empty result; null/scalars fail. This experimental package also caps selected input and aggregate output at 16 MiB per stage. An aggregate limit failure happens after item processing and does not undo deliveries; receipts remain in the journal. These are protective ceilings, not certified workload sizes.

Fixtures without `items` apply to every item. Supplying `items` requires a fixture for every encountered ordinal; a missing fixture fails without falling through to real systems. Output fixture arrays remain in destination order, with an optional `items` object inside each destination. Connector traces carry `itemOrdinal`. Item-specific inspection and recovery controls are planned in [M7.5](../../item-inspection-recovery-design.md).

Retries use the step's `retry` policy separately for each item. Completed items and destinations cannot be repeated by an ordinary retry. If an item exhausts its policy or requires review, the parent stops admitting further calls; already in-flight work can remain uncertain. Until item-level recovery is implemented, the experimental profile cannot use the ordinary whole-execution resume/replay endpoints. A bounded parent stop conservatively marks incomplete expanded stages for review when it cannot prove the absence of a writing call.
