# Elwood: live-platform handoff for another project and AI session

Prepared 6 October 2026 for a project that will adopt Elwood **after its integration platform is released**.

**How to read this document:** this is the intended release contract and integration design guide, not a claim that the platform is already production-ready. The transformation language is already used in production; the surrounding integration platform is being completed through six milestones. Descriptions below use the target live behavior. Concrete HTTP examples follow the current schema; check them against the version actually released before deploying. Native cloud packages, SQL, external RabbitMQ and some authentication/interface details still require their release-specific documentation. Do not invent YAML fields for those capabilities.

## 1. What Elwood is

Elwood is an open-source, self-hosted integration platform intended as an alternative to products such as MuleSoft, SnapLogic and Boomi. Its central objective is to let an organization own its integrations and choose its infrastructure without tying business logic to one vendor's platform.

There are two related parts:

| Part | Responsibility |
|---|---|
| **Elwood DSL** | Transform data: navigate JSON, filter collections, reshape objects, aggregate, look up related records and combine data. |
| **Elwood pipelines** | Coordinate an integration: accept a trigger, call external systems, run transformations, deliver results, persist progress, apply retry policy and expose execution history. |

The portal provides code-based authoring, validation, testing, deployment and operational inspection. The first release targets developers comfortable with YAML and scripts; a visual drag-and-drop designer is not a prerequisite.

Repositories:

- [elwood](https://github.com/max-favilli/elwood): language engines, CLI, pipeline runtime, connectors and hosting components.
- [elwood-portal](https://github.com/max-favilli/elwood-portal): authoring and operations interface.

An organization installs and operates Elwood itself. The target is native/serverless deployment packages using the selected cloud provider's services. Pipeline business logic should remain portable across supported packages. **Which cloud providers and internal brokers ship in the first release is not yet fixed**; do not assume every hyperscaler has a ready package. A hosted trial or commercial service is a secondary objective and is not required to use the self-hosted platform.

## 2. Where it fits in your application

Your application continues to own its domain data, business transactions and public API. Elwood coordinates exchanges between it and other systems.

For example:

1. Your application creates an order and sends an event to an Elwood HTTP endpoint.
2. Elwood transforms the order into a CRM request and calls the CRM.
3. Elwood captures the CRM response.
4. Elwood combines the original order with the CRM-assigned identifier and sends a differently shaped request to another system.
5. Operators can inspect the execution and recover failed work according to the pipeline's policy.

Elwood can also start from an external message broker. A representative product integration consumes an event from a third party's RabbitMQ, calls a Microsoft SQL Server stored procedure, combines the event and returned rows, then calls SAP through PI/PO. The precise SQL result-set, RabbitMQ subscription and SAP interface configuration must come from the released connectors. SOAP and TQL are identified integration needs whose exact interface requirements still need agreement.

**External and internal brokers are separate.** Consuming a third party's RabbitMQ does not require Elwood's own execution queue to use RabbitMQ. An Azure installation could use Azure Service Bus internally while integrating with RabbitMQ externally.

## 3. The pipeline model

A pipeline is a YAML definition plus any referenced `.elwood` scripts. These are published together as an immutable revision.

```text
Incoming HTTP request or external message
  -> trigger source
  -> dependent pull sources and their transformations
  -> Intermediate Data Model (IDM)
  -> output transformations
  -> external destinations and/or synchronous HTTP response
```

- A **trigger source** receives the original payload.
- A **pull source** calls another system and contributes its response to the integration. Despite the name, it may issue a POST and cause a write. Use this when later steps need the response.
- `depends` names source dependencies. Declare dependencies explicitly whenever a source needs another source's data or must run after its side effect.
- A **source map** transforms that source's payload and contributes the result to the IDM.
- The **IDM** is the JSON data assembled from completed source results.
- An **output map** transforms the completed IDM into a destination's schema.
- A **destination** receives an output. Use a pull source instead when downstream processing needs that call's response.

Source results that are objects merge at the top level. This is not an automatic relational join or deep merge. Prefer explicit namespaces such as `{ order: $ }` and `{ crm: $ }`; unrelated sources should not overwrite the same keys. Dependencies determine the source stages, and merging follows the pipeline's deterministic source order within those stages. An output does not modify the IDM.

### Evaluation context

| Context | Meaning |
|---|---|
| `$` in a trigger map | The incoming payload. |
| `$` in a pull-source map | That connector's returned payload. |
| `$idm` in a source | The IDM assembled from earlier dependency stages. |
| `$` / `$idm` in a request-body expression | The available IDM before that request. |
| `$` in an output map | The completed IDM, or the value selected by that output's optional `path`. |
| `$idm` in an output | The complete IDM, even if `path` selected a smaller value. |
| `$source` | Source metadata supplied by the runtime/connector; for example an HTTP pull response's `http.statusCode`. Check the released connector for other fields. |
| `$execution` | Execution identity: `id`, `step`, and `revision`, useful for correlation and request keys. |
| `$error` in `failureResponse` | The failed step, kind, messages and uncertain-outcome information. |

Host-provided bindings are part of pipeline evaluation. A standalone playground expression only has the input and bindings supplied to that evaluation; it does not automatically have a live pipeline's `$idm` or credentials.

## 4. DSL scripting essentials

Elwood combines JSONPath navigation, pipe operators and named lambda expressions. It has implementations for .NET and TypeScript. It is its own language: do not write JavaScript or assume JavaScript methods are supported. Keep orchestration and connector configuration in the pipeline.

An expression returns its value directly:

```elwood
$.items[*]
  | where item => item.active
  | select item => {
      name: item.name,
      upper: item.name.toUpper()
    }
```

Use named parameters such as `item => item.active`. Do not use the invalid shorthand `where .active`. `$` remains the evaluation's root input; use the named parameter for the current collection item.

A script can bind intermediate values and return a result:

```elwood
let active = $.items[*] | where item => item.active
return {
  names: active | select item => item.name,
  count: active | count
}
```

Useful patterns include object construction, `where`, `select`, `orderBy`, `groupBy`, `count`, `sum`, and `indexBy`. Read properties with `$.customer.email` or bracket notation. Strict access fails for missing properties; use optional access deliberately, for example `$?.customer?.email`, when absence is allowed. Returning `null` is a legitimate result and differs from a missing script or failed evaluation.

For repeated keyed lookups, build an index once rather than scanning a collection for every record:

```elwood
let productsBySku = $.products[*] | indexBy product => product.sku
return $.lines[*] | select line => {
  sku: line.sku,
  quantity: line.quantity,
  product: productsBySku[line.sku]
}
```

Successful evaluations may carry warnings, including repeated-scan warnings. A warning is recorded and displayed without changing success into failure. An error stops the affected step; Elwood must never silently send the original payload because its transformation failed.

## 5. Complete HTTP integration example

This example receives an order, creates a CRM record, then sends an enriched event to a second API. The `.test` hosts below are placeholders. Replace them with your test systems before selecting real-system mode. Mock mode supplies all connector responses locally.

Suggested bundle:

```text
order-export/
  pipeline.elwood.yaml
  crm-request.elwood
  delivery.elwood
  input.json
  mocks.json
```

### `pipeline.elwood.yaml`

```yaml
version: 2
name: order-export
description: Send an order to CRM, then deliver its assigned identifier
mode: async
ttlSeconds: 2592000

sources:
  - name: order
    trigger: http
    endpoint: /order-export
    map: '{ order: $ }'

  - name: crm
    trigger: pull
    depends: order
    retry:
      mode: manual
    from:
      http:
        url: https://crm.example.test/orders
        method: POST
        headers:
          Authorization: $secrets.crmAuthorization
          Idempotency-Key: '{$idm.order.id}:crm'
        body: crm-request.elwood
    map: '{ crm: $ }'

outputs:
  - name: delivery
    map: delivery.elwood
    retry:
      mode: manual
    destinations:
      http:
        - url: https://events.example.test/orders
          method: POST
          headers:
            Authorization: $secrets.eventsAuthorization
            Idempotency-Key: '{$idm.order.id}:delivery'
```

The example deliberately requires an explicit recovery request for eligible connector failures. Choose an automatic policy once the destination's duplicate-handling contract is known. An `Idempotency-Key` header only has an effect if the receiver implements it. For a reusable production pipeline, choose a key that includes the relevant tenant, operation and business-event version; an order ID alone may not distinguish an order-created event from a later update.

### `crm-request.elwood`

```elwood
return {
  externalOrderId: $idm.order.id,
  customerEmail: $idm.order.customer.email,
  lines: $idm.order.items[*] | select item => {
    productCode: item.sku,
    quantity: item.quantity
  }
}
```

### `delivery.elwood`

```elwood
return {
  orderId: $idm.order.id,
  crmOrderId: $idm.crm.id,
  email: $idm.order.customer.email,
  total: $idm.order.total
}
```

### `input.json`

```json
{
  "id": "order-42",
  "customer": { "email": "buyer@example.test" },
  "items": [{ "sku": "SKU-1", "quantity": 2 }],
  "total": 39.8
}
```

### `mocks.json`

Mock source keys match source names. Output arrays follow the configured destination order. `expectBody` asserts the outgoing JSON, so a test checks the actual transformation as well as supplying a response.

```json
{
  "sources": {
    "crm": {
      "statusCode": 201,
      "body": { "id": "crm-9001" },
      "expectBody": {
        "externalOrderId": "order-42",
        "customerEmail": "buyer@example.test",
        "lines": [{ "productCode": "SKU-1", "quantity": 2 }]
      }
    }
  },
  "outputs": {
    "delivery": [{
      "statusCode": 202,
      "expectBody": {
        "orderId": "order-42",
        "crmOrderId": "crm-9001",
        "email": "buyer@example.test",
        "total": 39.8
      }
    }]
  }
}
```

The application sends the input JSON to the deployed trigger URL. In the reference Runtime API, `endpoint: /order-export` is exposed at `POST /api/v1/trigger/order-export`; a cloud gateway can add a public prefix or hostname. Use the URL supplied by the installed runtime rather than assuming the YAML path is the entire public URL.

This async endpoint returns `202 Accepted` with an execution ID after durable acceptance. That response means Elwood has taken responsibility for processing; it does **not** mean the CRM and final delivery have succeeded. Execution status is available through the authorized management API and portal. Do not expose management credentials to a public browser client. If the application needs a business completion callback, design an explicit callback destination and application contract; do not assume an automatic webhook subscription exists.

## 6. Synchronous versus asynchronous execution

**Synchronous:** the caller waits for pipeline processing and receives the output marked `response: true`. Exactly one output provides the response. Its `responseStatusCode` expression can select the HTTP status. Other destination calls must also finish before the request is reported successful. Use this when the total operation fits within the installed runtime and gateway's request deadline.

**Asynchronous:** Elwood durably stores the accepted input, revision and processing intent, then returns HTTP 202 or acknowledges the external message. Workers perform the integration afterward. Async definitions have no `response: true` output. A crash or lost internal queue notification must not lose accepted work.

To adapt the example to synchronous operation, use `mode: sync`, mark the delivery output `response: true`, and optionally set `responseStatusCode: '200'`. The transformed delivery body then also becomes the caller's response after delivery succeeds. A separate response output is also possible.

Synchronous pipelines can author a failure response:

```yaml
failureResponse:
  statusCode: '502'
  body: '{ error: "Order export failed", step: $error.step, executionId: $execution.id }'
```

A failure response does not undo a successful earlier external write. Elwood does not provide a distributed transaction spanning the CRM and another API. If compensation is required, define that business process explicitly. Nor can the runtime respond on an HTTP connection that has already been lost.

## 7. Retries, durable progress and operator recovery

The maintainer chooses retry policy in YAML **per source or output**. There is no unconditional rule to repeat every failed external call.

An illustrative automatic policy is:

```yaml
retry:
  mode: automatic
  maxAttempts: 3
  delayMs: 1000
  backoffMultiplier: 2
  maxDelayMs: 60000
  statusCodes: [408, 429, 500, 502, 503, 504]
  transportErrors: true
  allowUncertainWrites: false
```

`maxAttempts` includes the first attempt in an automatic retry cycle. With `mode: manual`, eligible failures wait for an explicit recovery request. Without an automatic policy, Elwood does not silently retry connector failures. Transformation errors require investigation rather than repeatedly sending the same invalid data. The released schema remains authoritative for policy fields and defaults.

An external write may succeed even if its response times out or the worker dies before saving the result. In that case Elwood cannot infer the receiver's state. An automatic policy needs `allowUncertainWrites: true` to permit automatic repetition of such writes. Otherwise operator review is required. Set that option only when duplicates are acceptable or the receiver honors an appropriate idempotency contract. Even an HTTP error response does not prove a remote transaction was rolled back.

Three operations are distinct:

| Operation | What runs | Version and identity |
|---|---|---|
| Automatic retry | The eligible failed work, under YAML policy. | Same execution and pinned revision; a new attempt is recorded. |
| Resume | Continues an existing failed/paused execution from its saved progress. Successful earlier source results and checkpointed deliveries are reused. | Same execution and original revision. |
| Replay | Starts again from the retained original input. Earlier external calls can run again. | New execution linked to the original; original revision by default, or an explicitly selected published revision. |

A successful intermediate response is saved before its map runs, so resuming after a map failure need not repeat that successful external write. If the script itself needs correction, deploy a corrected revision and explicitly replay with it; editing a draft cannot alter the failed execution's original script.

Attempt history records timings, errors, warnings, calls and uncertain outcomes. Duplicate queue notifications must not create overlapping attempts of the same protected work. This is **at-least-once processing with checkpoints and controlled recovery**, not a promise of exactly-once effects in third-party systems.

Use a stable business-event key when duplicate prevention must cover separate triggers or replays. `$execution.id` is stable across retries/resume but changes on replay. The reference HTTP runtime also accepts an `Idempotency-Key` on incoming async requests: repeating the same key and payload for the same pipeline/trigger reuses the retained execution; conflicting payloads are rejected. This deduplication is bounded by retention and does not replace the application's business-level contract.

## 8. Drafts, testing and deployment

The workflow is:

1. **Save a draft.** Store YAML and its scripts without changing the live pipeline.
2. **Validate.** Check schema, references, dependencies and supported configuration.
3. **Test locally.** Supply a trigger payload and explicitly choose real systems or mocks.
4. **Inspect.** Check transformed data, steps, diagnostics and outgoing calls.
5. **Deploy explicitly.** Publish and activate the selected immutable revision.

Local testing runs the definition to a result synchronously even when its deployed mode is async. It uses the same transformation and processing rules without rewriting the saved mode. It is not a distributed crash/recovery test.

- **Mock mode:** uses explicit connector fixtures and assertions. Missing fixtures fail the test; they must never fall through to a real system. Host secrets are not needed.
- **Real-system mode:** actually calls the configured systems and may perform writes. Use dedicated test accounts and data. Host-owned test secret grants are separate from live execution grants.

The playground evaluates DSL expressions/scripts against supplied input. Use **Test draft** for the complete pipeline and real/mock connector selection.

Publishing a new version changes subsequent executions. An already accepted execution retains its original YAML and scripts, including during retry or resume. Referenced scripts belong to the same published bundle; do not rely on a mutable file being read from the working directory during execution.

## 9. Operations, credentials and retention

Management access is authenticated and scoped to pipelines and operations such as read, write, test, deploy and recover. Trigger authentication is a separate contract for calling applications. Configure it for the installed environment; the sample intentionally omits deployment-specific inbound credentials.

Secrets belong to the installation's secret provider and pipeline-specific grants. YAML uses references such as `$secrets.crmAuthorization`, not committed passwords. Do not put management tokens or external-system secrets in browser code, sample payloads or the IDM. Verify the released connector's OAuth2/Basic authentication configuration rather than building a guessed token flow.

The portal exposes execution status, revision, source/output data, diagnostics, individual attempts and recovery controls. HTTP inspection includes method, URL, request/response headers and bodies, status and timing where available. Sensitive credential headers and query values are redacted before trace persistence. Payload bodies are not automatically anonymized; plan data access and retention accordingly. A worker crash can leave an incomplete trace whose missing response is itself relevant evidence.

The default retention is **30 days**, configurable per pipeline using `ttlSeconds`. Retention covers input, revision snapshots, step results, attempts and other stored execution artifacts needed for recovery. It starts when an execution becomes terminal, including failure or waiting for operator review; active processing/retry work is protected from ordinary expiry. Resuming reactivates the execution, and a later terminal outcome starts its retention window again. Operators must act before retained artifacts expire. Delivered output blobs also have a **30-day default**, with a pipeline default and per-destination overrides. Their retention clock starts at the object/version write; cleanup remains responsible even if execution history expires first. Source-owned input files are not deleted. Every supported cloud installation must enforce lifecycle/cleanup of outputs, versions, snapshots and orphaned temporary data and report cleanup failures. This output retention is part of the release target, not implemented by the current M5 connector; see [storage retention](storage-retention.md). A replay owns its own input/snapshot so expiry of the original does not break the replay.

The installation must provide persistent stores and supported queue/recovery adapters. Native provider packages must also document installation, permissions, upgrades, backups and recovery procedures. A local development server is not evidence that a cloud package has met its release criteria.

## 10. Instructions for the AI building the pet project

Use this document as architectural context. **The owner will integrate Elwood only after the platform is live.** Prepare the application's integration boundaries now; do not depend on an unreleased deployment or claim the integration is operational.

When the owner is ready:

1. Identify the application's entities, events, systems to connect, ordering requirements and which system owns each record.
2. Choose each integration's trigger and sync/async contract. Separate “accepted for processing” from “business operation completed.”
3. Define sample request/response schemas, authentication, timeouts, correlation identifiers and a business-event idempotency strategy with each receiver.
4. Produce versioned YAML, `.elwood` scripts, synthetic trigger inputs, connector mocks and expected results. Use explicit source dependencies and namespaced IDM fields.
5. Specify which failures may retry automatically, which require an operator, and how uncertain writes are checked. Define compensation separately if the business process requires it.
6. Test happy paths, bad input, mapping failures, authentication failures, throttling, HTTP errors, timeouts, duplicate events and partial completion. Exercise deployed restart/recovery separately from local mocks.
7. Configure host secrets and permissions, validate against the actual released schema, test against controlled real endpoints, then deploy an immutable revision.

Ask the owner for missing business requirements. Do not assume this pet project uses the e-commerce, CRM, Emarsys or SAP examples; those demonstrate composition, not mandatory dependencies. Do not assume a visual designer, scheduler, arbitrary loop/fan-out configuration, all-cloud availability or every connector mentioned in older reference material is part of the released contract.

## 11. Source material to consult at adoption time

This handoff is self-contained, but the released version's documentation takes precedence for exact syntax and supported capabilities. The links below point to `main` and may change; pin them to the adopted release when implementing.

- [DSL syntax reference](https://github.com/max-favilli/elwood/blob/main/docs/syntax-reference.md)
- [Pipeline YAML reference](https://github.com/max-favilli/elwood/blob/main/docs/pipeline-yaml-reference.md)
- [Changelog](https://github.com/max-favilli/elwood/blob/main/docs/changelog.md)

Additional workspace guides being developed alongside the platform are `docs/product-direction.md`, `docs/implementation-plan.md`, `docs/management-and-deployment.md` and `docs/local-testing.md`. They may not be available remotely until the local work is committed and published. The milestone sequence is execution correctness; drafts/versioning/security; local testing; durable recovery; representative connectors; native cloud installations. This document does not certify completion of that sequence.
