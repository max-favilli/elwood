# Public HTTP samples for M3

These pipelines exercise the same draft against public test services in **Real systems** mode or bundled fixtures in **Mocks** mode. The input is synthetic. None requires credentials or an account with the default endpoints.

## Open in the portal

Choose **Pipelines → New Pipeline → Start from**, select a sample and give the new draft a unique ID. The portal adjusts its name and trigger endpoint for that ID. Creating the draft does not deploy it or send requests.

Open **Test draft**, select **Mocks** or **Real systems**, then **Run**. The sample input and complete fixtures are already loaded; there is no need to generate an empty fixture template. **Reset sample input and mocks** restores the bundled values. Keep the `# sample:` comment to retain that preset association when reopening the panel. If you change step names or transformations, update the fixtures to match.

| Sample | Flow | Expected outcome |
|---|---|---|
| [Order echo](sample-echo-order/pipeline.elwood.yaml) | HTTP input → transform → POST to Postman Echo → return echoed JSON | Success; response contains the original synthetic order ID, customer name and total. |
| [Cart creation and delivery](sample-cart-chain/pipeline.elwood.yaml) | HTTP input → POST a simulated cart to DummyJSON → merge returned ID with original order → POST to Postman Echo | Success; delivery includes `orderId`, `cartId` and `cartTotal`. |
| [HTTP 503 failure](sample-http-error/pipeline.elwood.yaml) | HTTP input → GET httpbin `/status/503` | Intentional failure at `remote`; the response output stays pending. |
| [HTTP timeout](sample-http-timeout/pipeline.elwood.yaml) | HTTP input → GET httpbin `/delay/5`, with a 1000 ms deadline | Intentional failure at `remote`; the response output stays pending. |

Each directory contains `pipeline.elwood.yaml`, `input.json`, `mocks.json`, `expected.json` and `sample.json` (portal title and explanation). There are no separate `.elwood` scripts in these examples. `expected.json` describes the deterministic **mock** outcome.

Cart creation is declared **async**, demonstrating local synchronous testing without changing that definition. Live deployment of async triggers still requires M4. For now, use **Test draft**, not the deployed trigger endpoint, for this example.

## What the services provide

- [Postman Echo](https://learning.postman.com/docs/developer/echo-api) echoes request details. These samples use `POST https://postman-echo.com/post` with JSON and read its `json` response field in the echo source.
- [DummyJSON carts](https://dummyjson.com/docs/carts) simulates cart creation at `POST https://dummyjson.com/carts/add`; it does not persist the new cart. IDs and catalog prices can differ from our fixed fixtures. Use the returned ID in the next step rather than hard-coding one.
- [httpbin](https://httpbin.org/) supplies chosen HTTP status codes and delayed responses. Public availability, proxies or network errors may change the observed real-mode failure. A timeout can appear as “The operation was canceled.” in the current HTTP connector.

Mock mode never contacts these services. The timeout fixture raises a simulated transport error immediately; it does not wait or test wall-clock timing. Real mode exercises the HTTP request deadline. These cases test M3 diagnostics and step history; automatic retries and recovery remain M4.

## Inspect delivery with Webhook.site

The cart example uses Postman Echo as a destination so it works without setup. For an independent browser view of the outgoing payload:

1. Visit [Webhook.site](https://webhook.site/) and copy **Your unique URL**. Use its receiving URL, not the browser's `#!/view/...` address.
2. In the cart pipeline, replace the URL under `outputs → delivery → destinations → http` with that URL. Leave the DummyJSON source URL unchanged.
3. Run **Test draft → Real systems**. The Webhook.site page should display a POST whose JSON body has `orderId`, `cartId` and `cartTotal`.
4. Switch to **Mocks** to verify the same transformation offline. The destination fixture is matched by output name and destination index, so changing only the receiving URL does not require a fixture change.

At the time of writing, [Webhook.site's free URLs](https://docs.webhook.site/) expire after seven days and accept up to 100 requests. Use synthetic payloads; the destination is a public test service. Elwood does not create a Webhook.site URL, store a personal receiving URL in these examples, or publish a deployment automatically.

## Repeatable checks

`PublicHttpSampleTests` reads the actual YAML/input/fixture files and verifies their expected outputs or failing step through the pipeline executor. These tests never need the public network. Portal tests verify sample copying, unique endpoint substitution and the bundled data.

The portal keeps a checked-in copy at `src/data/pipeline-samples.json`, so it works when cloned alone. After editing these canonical backend files, run `npm run samples:sync` in the sibling `elwood-portal` repo. Regenerate and review the bundle in the same change.

Manual verification on 6 October 2026 exercised all four samples through the Runtime API in both modes: echo and cart delivery succeeded; the error source returned 503 and the delayed source hit its deadline. The browser walkthrough also created an independently named echo draft, loaded its fixtures automatically, and ran it successfully with both modes. No endpoints were deployed. Webhook.site delivery remains an optional setup recipe; it was not provisioned during verification.
