# Local pipeline tests (M3–M5)

Local tests execute a draft synchronously through `SyncExecutor`, whether its declared mode is `sync` or `async`. The declaration is preserved: an async draft needs no response output, and it is not rewritten or deployed. Tests use the current editor YAML and scripts, including unsaved changes. The execution retains the exact content hash and bundle it used.

## Portal workflow

1. Open a pipeline draft and choose **Test draft**.
2. In **Connectors**, explicitly choose **Mocks** or **Real systems**. There is no default and no mixed mode.
3. Enter the trigger JSON in **Input payload**. For mocks, use **Replace fixtures with template**, then edit the generated response bodies in **Mock fixtures**. Generating a template replaces the current fixtures; it does not contact connectors.
4. Choose **Run**. Inspect **Steps and data** for statuses, diagnostics (including successful warnings), transformed source/output data, merged data, and connector calls.
5. The same inspection is available later under **Executions**. Test runs identify their connector mode and tested revision. Saving and deploying remain separate actions.

Real-system tests can change external systems. They use the host's pipeline-specific `Test` secret grants, never its `Run` grants. HTTP authentication, SQL connection strings and Blob settings resolve through that scoped provider; host identities additionally require connection grants. Mock tests do not resolve secrets or contact a real connector. They substitute HTTP responses at a terminal in-memory HTTP handler, while using the same HTTP connectors and pipeline evaluation/ordering rules. Mocks do not verify endpoint reachability or credentials.

Currently supported: zero or one JSON HTTP/queue trigger, HTTP/SQL/Blob pull sources and destinations. The supplied trigger payload replaces the incoming event; no broker listener is started. Multiple trigger sources and unsupported connectors are rejected in both modes. A supplied RabbitMQ event or one-element Event Grid array exercises transformations and connector calls; broker ACKs and webhook handshakes require deployed-ingress tests. Local tests apply authored retries through the shared step engine, but do not simulate process interruption, broker redelivery or cloud durability.

Start with the runnable [order pipeline](examples/local-testing/pipeline.elwood.yaml), [trigger input](examples/local-testing/input.json) and [fixtures](examples/local-testing/mocks.json). Mock mode runs this async example without configuring any secrets.

## Public HTTP sample pipelines

The [M3 public HTTP samples](examples/public-http/README.md) cover order echo, simulated cart creation followed by delivery, HTTP 503 and timeout failures. Create them through **New Pipeline → Start from**; their synthetic input and matching mock fixtures load automatically in **Test draft**. Explicit mode selection still applies. The cart example also documents optional Webhook.site delivery inspection.

The [M5 connector samples](examples/connectors/README.md) add authenticated orders, product SQL/SAP and Deldo Blob/SQL/snapshot flows. SQL fixture bodies are row arrays; text Blob bodies are strings. SQL output bodies supply receipt rows; Blob output bodies supply receipt objects. The portal shows connector operation, request metadata and returned data alongside existing HTTP detail. See [connector contracts](connectors.md).

## Fixtures

Fixtures are JSON, separate from the pipeline YAML. `sources` keys are HTTP/SQL/Blob pull source names. `outputs` keys are names with destinations. Fixture arrays follow execution order: HTTP, Blob, SQL, then array order within each connector type (YAML mapping-key order is irrelevant). All external connector names and destination counts must match exactly. Missing, unknown, null or misspelled fixtures fail validation; they never fall through to a real connector. Response-only outputs and trigger sources need no fixture.

```json
{
  "sources": {
    "crm": {
      "statusCode": 201,
      "contentType": "application/json",
      "body": { "id": "crm-42" },
      "expectBody": { "externalId": 42 }
    }
  },
  "outputs": {
    "emarsys": [{
      "statusCode": 202,
      "expectBody": { "orderId": 42, "crmId": "crm-42" }
    }]
  }
}
```

Every fixture supports:

| Field | Meaning |
|---|---|
| `body` | Response JSON, default `null`. For non-JSON content types, supply a string. |
| `contentType` | Response MIME type, default `application/json`; e.g. `text/xml`, `text/csv`, `text/plain`. |
| `statusCode` | HTTP status 100–599, default 200. Existing connector acceptance rules apply. |
| `headers` | Response headers, default `{}`. |
| `error` | Optional simulated transport failure message. |
| `metadata` | SQL/Blob metadata; `sql`/`blob` subobjects populate `$source` on pulls. |
| `retryable`, `outcomeUncertain` | For SQL/Blob simulated errors, whether a retry is eligible and whether a write may have happened. Defaults: true/false. |
| `expectBody` | Optional structural JSON or raw-string assertion on the outgoing body. Object key order/formatting do not matter. Explicit `null` asserts a JSON null body; omission skips assertion. |

Assertions run after body transformation and before the fixture response. Failed assertions or simulated failures are attributed to the source/output in history. Source failures stop dependent stages. Output data is retained even if delivery fails. Fixtures are frozen when testing starts.

## Management API

All requests need the existing management bearer token and relevant pipeline scope.

| Request | Permission / behavior |
|---|---|
| `POST /api/pipelines/{id}/test-template` with `{content?:{yaml,scripts}}` | `test`: returns `{revisionId,mocks}` from the supplied or saved draft without executing it. |
| `POST /api/executions` | `test`: requires `{pipelineId,payload,testMode:"real"\|"mock",mocks?,content?,expectedRevisionId?}`. Mock mode requires fixtures; real mode rejects them. |
| `GET /api/executions/{id}/inspection` | `read` on the execution's pipeline: returns stored data and state. An inaccessible execution returns 404. |

Execution responses contain `success`, `errors`, `outputs`, `responseStatusCode`, `revisionId`, `testMode`, `executionId` and `inspection` (null without pipeline `read` permission). HTTP 200 acknowledges completion of the test request; check `success` for the pipeline outcome. Missing/unknown test mode and invalid fixtures produce HTTP 400 before any connector call. If `content` is absent, `expectedRevisionId` can reject stale saved-draft tests. With `content`, the exact submitted snapshot is hashed and tested.

Inspection includes the frozen pipeline, test input/fixtures, source/output documents, IDM and connector calls. Documents have `{available,value}` so missing data is distinct from a legitimate JSON `null`. Older executions can lack documents.

In **Steps and data → Connector calls**, each HTTP source/output call shows its method, resolved URL, start time, elapsed time, effective timeout, request headers/body, and response status, protocol version, headers/body. Rejected HTTP responses are captured before status validation; transport failures retain request details and an error without inventing a response. Header values are arrays so repeated headers are preserved. These are application-level exchanges, not a wire capture: transport-generated headers and intermediate redirects are not individually traced. Mock exchanges show the terminal handler's `mock.invalid` URL and synthetic responses.

Sensitive header values (authorization, cookies, API keys, tokens, secrets and similar credential fields) are replaced with `[REDACTED]` before trace and fetched-checkpoint storage. Credential query parameters are redacted; URL user information and fragments are removed. Request/response bodies, input/fixtures, and authored YAML retain their data and may themselves contain sensitive values; this is not a general payload scrubber. Inspection retains pipeline read authorization. Use secret references in YAML instead of literal credentials. Old executions cannot acquire the newly captured HTTP details retroactively; run the test again.

The existing CLI `pipeline run` remains the supplied-input transformation runner; the M3 real/mock end-to-end test interface is the management API and portal. For .NET embedding, create a fresh `LocalTestSession` per run and pass it to `SyncExecutor`. Do not reuse a session across executions.

## Verification

`LocalTestingTests` compares sync/async definitions across both modes, verifies isolation using a secret provider that throws and a network handler that counts calls, and covers body assertions, accepted/error HTTP statuses, missing fixtures and inspection persistence. `scripts/runtime-api.test.mjs` runs a separate API and loopback service to verify actual HTTP transport, permissions, no network fall-through, and unchanged drafts/deployments. Portal transport tests require explicit mode/fixtures.

Run `dotnet test dotnet/tests/Elwood.Pipeline.Tests/Elwood.Pipeline.Tests.csproj`, build the Runtime API, then `node --test scripts/runtime-api.test.mjs`. Run `npm test`, `npx tsc --noEmit`, `npm run lint` and `npm run build` in `elwood-portal`.

## Retry and recovery behavior

M4 local tests use the same step engine and authored retry policy as live executions. Attempts and call details are recorded. Test records do not expose live resume/replay controls or enter the background recovery queue; rerun the draft test explicitly. Deployed executions use the recovery workflow documented in [durable processing](durable-processing.md).

Body previews are limited to 64 KiB per request/response by default. Configure `Elwood:Inspection:TestBodyLimitBytes` from 0 (disabled) to 1048576. Inspection reports byte counts and truncation markers; fixture assertions, transformations and receipt/checkpoint documents still use the complete data. Production execution state stores metadata only, and production runs do not create a test-context document.


## Offline CLI boundary

`elwood run` uses `PipelineExecutor` to transform supplied source files. It does not execute runtime connectors, retries, durable acceptance, destination delivery or recovery. Use the portal/API **Test draft** workflow when verifying runtime behavior, even for an async pipeline.

The offline CLI and runtime share the DSL engine but supply different context bindings. In CLI source maps, `$source` comes from a supplied source envelope or minimal generated metadata; `$execution` is not supplied. At runtime, `$execution.id` identifies the retained execution, and fetched sources add HTTP/SQL/Blob metadata. Output maps and destination expressions must not assume a current `$source`: use the mapped IDM to carry required source metadata forward. A script that needs `$execution.id` must be tested through the runtime or receive an explicit binding in a standalone DSL test.

The CLI's stored pipeline snapshot is an offline inspection artifact, not the durable runtime snapshot format. CLI executions cannot be resumed or replayed through the Runtime API. This distinction does not change DSL syntax or evaluation semantics.
