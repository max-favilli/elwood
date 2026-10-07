# Representative connector samples

These synthetic examples are available in the portal under **New Pipeline → Start from**. Create a draft, open **Test draft**, and choose **Mocks** to run immediately with the bundled input/fixtures. Save, validate, test and deploy remain separate actions.

| Bundle | Behavior | Real-system prerequisites |
|---|---|---|
| [Authenticated order](sample-order-auth/pipeline.elwood.yaml) | HTTP order → OAuth2 CRM → Basic-authenticated Emarsys HTTP; CRM ID appears in delivery | HTTP business/token endpoints and scoped Test secrets |
| [Product/SQL/SAP](sample-product-sql/pipeline.elwood.yaml) | Supplied product event → parameterized SQL lookup → SAP HTTP with Basic auth | `dbo.GetProduct(@id int)` returning `sku`, `quantity`; SAP test endpoint; scoped Test secrets. Deployment additionally needs an existing external RabbitMQ queue and Run secrets |
| [Deldo supplier import](sample-deldo-stock/pipeline.elwood.yaml) | BlobCreated → conditional Blob CSV read → transformation → SQL JSON import + version-named Blob snapshot | Existing `inbound`/`analytics` containers, `catalog.usp_ImportOffers(@payload nvarchar(max))`, scoped secrets. Event Grid subscription is a later deployment step |

The Deldo data is synthetic: 11 rows produce five offers and six rejected rows with reasons. Expected documents are in its `expected/` directory. Column names are placeholders until a real supplier file is reviewed. The shared scripts and fixture were copied from the pet project on 6 October 2026; that repository remains the owner of its supplier/catalog contract.

Real-system tests execute writes. They use the supplied trigger JSON and do not start RabbitMQ consumers or Event Grid subscriptions. Configure the exact secret keys used by the chosen YAML in host `Elwood:SecretGrants:<pipeline-id>:Test`; use `Run` grants for deployments. Host identities need separate connection grants. See [connector contracts](../../connectors.md) for all fields and defaults.

Deldo snapshots explicitly set `overwrite: true`; the connector otherwise defaults to create-only. ETag mismatch fails visibly and does not load replacement bytes. The integration test proves that a successful SQL import is not repeated when a failed snapshot destination is fixed and the execution resumed.

Automated API tests run all three bundles with strict mocks and the authenticated order against controlled loopback endpoints. Connector integration tests exercise the Deldo flow against real SQL/Azurite and the product flow against SQL/HTTP; the broker variant and redelivery test require RabbitMQ. The workflow's connector job supplies these services. This is local/CI evidence, not a deployed Azure installation.

After changing sample YAML, scripts or fixtures, run `npm run samples:sync` and `npm test` in the sibling portal repository to refresh and verify its bundle.
