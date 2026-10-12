# Elwood integration platform implementation plan

Current task-by-task progress is tracked in the [master checklist and WBS](master-checklist.md). This document defines the delivery sequence and design rationale, with dated historical verification evidence below.

**Current status — 12 October 2026: the full plan is not complete.** M1–M5 are implemented. M6's Azure runtime, installer and review fixes are verified, and `v0.7.26` is published; the complete-day telemetry measurement, temporary-lab deletion and final reported cost remain open. M7 is active: coordination and durable journals pass CI, and the isolated item runner is implemented, but distributed execution is not enabled in a host. Eagle and Deldo consumer installation and real-world acceptance remain open; Eagle's concurrent work-item requirements depend on M7. Follow the confirmed [product direction](product-direction.md).

The dated implementation notes below record what was true at each checkpoint. Their earlier statements about unimplemented features, pending tests or unpushed commits are historical; use the current status and master checklist for remaining work.

This plan covers the backend and the sibling `elwood-portal` repository. Each milestone ends with a demonstrable behavior and focused automated checks. Milestones describe proposed delivery order; they do not expand the agreed product scope.

## Git baseline before milestone implementation on 6 October 2026

Both checkouts match their fetched `origin/main`: backend `672ef68`, portal `75052c3`. Local portal alignment work and planning documents remain uncommitted and must be preserved.

The DSL is now 0.7.25. Since the September assessment it gained UTF-8 input entry points, streaming output, lookup optimizations, repeated-scan warnings and `indexBy`. The Runtime API script-test endpoint uses the new streaming path. The pipeline executors have not changed since that assessment, so the previously reproduced error-handling defects remain relevant.

Successful evaluations can carry `Warning` diagnostics since 0.7.24. The existing language behavior, including `ScanWarningThreshold` on `ElwoodEngine` and `indexBy`, is documented in the [changelog](changelog.md) and the syntax reference sections [Repeated scans](syntax-reference.md#repeated-scans) and [Lookup by key](syntax-reference.md#lookup-by-key). The work below preserves and surfaces those semantics; it does not change the DSL.

The portal still pins DSL 0.7.21. Its dependency, examples, editor language support and display of successful-result warnings need alignment with 0.7.25. Earlier test totals are historical baselines; run current checks when implementing each change rather than treating them as current certification.

## Delivery sequence

| Milestone | Backend work | Portal work | Completion evidence |
|---|---|---|---|
| 1 Execution correctness | Preserve errors and warnings per step, share evaluation behavior, persist failures and step results, validate supported configuration | Report execution failures accurately and display successful-result warnings | Failed transformations cannot become successful deliveries; successful and failed runs have accurate history, including warnings |
| 2 Drafts, published versions and management security | Separate drafts, immutable revisions and active deployment; pin execution revision; enforce management authentication/authorization, path containment and scoped secret access | Save draft, validate without publishing, explicit deploy; respect management permissions | Editing a draft leaves live behavior unchanged; old executions retain their original scripts; management security checks pass before team use |
| 3 Local testing | Run sync or async definitions locally with explicitly selected real or mock connectors | Test selected draft, select real or mock mode, inspect outputs and steps | The same draft follows the same processing rules in either mode; mocks never fall through to real systems |
| 4 Durable processing and recovery | Durable acceptance, queue recovery, per-step retry policies, resume/replay, retention | Show attempts, failures and recovery actions | Accepted work survives interruption; duplicate messages do not launch overlapping attempts unchecked |
| 5 Representative connectors | HTTP authentication, SQL stored-procedure sources and destinations, external RabbitMQ triggers, required SAP interface support, Blob reads/writes and Event Grid ingress | Configure connections; mock and inspect HTTP, SQL, Blob and message interactions | Reproduce both the original order/product integrations and Deldo Blob → SQL + Blob imports against controlled systems, including recovery |
| 6 Native cloud installations | Provider-specific hosting, queue/state/document adapters and infrastructure, enforced internal/output blob retention, including a small Azure installation without mandatory Redis | Connect authoring and deployment to the hosted runtime | Install, recover and operate native deployments; prove default 30-day storage cleanup and measure the small Azure profile against the pet project's total budget |
| 7 Distributed work items and Redis | Independent concurrent stages/items, durable checkpoints and Redis reconciliation, explicit fan-out, aggregation, bounded recovery/retention and a consumer deployment profile | Paginated item inspection, item recovery and real/mock testing of distributed definitions | Run the Eagle-style workload across independent workers; verify restart/failover, uncertain writes, finite retention and a versioned consumer installation |

Milestone 2 owns management authentication/authorization, path containment and scoped secret access. These are completion requirements before the portal and API enter shared team use or an externally reachable installation. Add automated API/browser coverage and CI throughout the sequence, rather than postponing verification to the cloud milestone.

## Milestone 7 — Distributed work items and Redis

M7 covers the Eagle requirement clarified during M6: one integration may fan out thousands of work items, with concurrent sources and frequent shared status updates. M6's bounded Azure profile serializes steps within each execution. Its completion does not satisfy this additional concurrency requirement. The distributed profile is explicit; existing deployments keep their current execution semantics.

Redis provides shared coordination and cached status. Durable manifests, checkpoints and storage-enforced ownership remain authoritative for recovery when Redis loses data. Pipeline revisions stay pinned; uncertain external writes follow the pipeline's automatic/manual retry policy. Source fan-out preserves item identity, while output fan-out is explicit and does not change the existing meaning of output `path`. No DSL behavior changes are required.

| Package | Current state | Remaining delivery |
|---|---|---|
| 7.1 Coordination contract and Redis adapter | Complete as an experimental component; real Redis protocol tests pass | Production activation depends on the following packages |
| 7.2 Durable manifests, checkpoints and reconciliation | Complete as a component; local and Redis/Azurite CI checks pass | Provider failover/load certification remains 7.7 |
| 7.3 Runtime integration | Item runner, parent scheduling/recovery and whole-step local real/mock parity pass CI | Host registration requires 7.5; expanded-item aggregation and its local test parity belong to 7.4 |
| 7.4 Explicit YAML fan-out | Complete; local checks and all CI jobs pass (`1fa6bf7`) | Source/output expansion, bounded scheduling, ordered aggregation and local real/mock parity; production activation remains gated on 7.5 |
| 7.5 Inspection, recovery and retention | Design drafted; implementation open | [API/portal contract](item-inspection-recovery-design.md), paginated item history, operator actions, finite physical cleanup, quotas and backpressure |
| 7.6 Shared pipeline/route cache | Open | Immutable bundle distribution, deployment invalidation and correct cold-cache behavior |
| 7.7 Failure and capacity verification | Open | Thousands of items across independent workers, duplicate delivery, restart/failover, uncertain writes, retention and measured provider costs |
| 7.8 Consumer release | Open | Redis infrastructure/configuration, versioned packages, installation smoke tests, tutorials and Eagle/Deldo handoffs |

**M7 is complete only when the full distributed profile can be installed and exercised through the runtime and portal with verified recovery, retention and operating limits.** Passing component tests is insufficient. The current item runner is not registered in the API or Functions host. Detailed contracts, checks and evidence are in [distributed-work-items.md](distributed-work-items.md); the [master checklist](master-checklist.md#m7--distributed-work-items-and-redis) tracks delivery.

The Elwood session owns reusable code, installation assets, documentation and releases. The Eagle and Deldo sessions own permanent provisioning, cost approval and their real integration acceptance tests. M6's scheduled measurement/cleanup can finish independently while M7 implementation proceeds.

## First work package

### Implementation progress on 6 October 2026

M1 implementation is present locally in both repositories, pending commit and review. The shared evaluator now retains per-step errors/warnings and fails missing script references; all three executors record terminal states, step timing and document references. The Runtime API and standalone evaluation API use the shared script classifier. Parser validation diagnoses unknown keys and unsupported execution settings, preserves documented destination aliases, and resolves HTTP body and response-status scripts. The portal uses 0.7.25, displays both warning transports and execution-history warnings, and distinguishes a failed execution envelope from a successful HTTP request. Runtime documents now use the local filesystem document store alongside execution state.

Verification includes regression tests across all three executors, mocked CRM-to-Emarsys flows, detached state-store reads/writes, an isolated Runtime API contract test, portal transport tests, and a browser check showing a successful result with a repeated-scan warning. Portal CI and a separate Docker-backed Azure adapter job are added. Docker is unavailable on this workstation, so the Azure job still needs its first CI run; the existing SSH.NET vulnerability warnings from its dependency graph remain outside this change. Distributed concurrency, durable acceptance/recovery and retries remain milestone 4 work. See the [current configuration limits](pipeline-yaml-reference.md#current-implementation-limits) before adopting the stricter validation.

Current checks: 109 pipeline tests (49 added), 374 Core tests, 21 CLI tests, 7 Parquet tests, 240 TypeScript engine tests, 118 portal tests and the Runtime API contract test pass. The .NET solution and portal production builds pass. Portal lint has no errors and four existing hook/unused-variable warnings. The CLI fixture required access to the user NuGet configuration outside the sandbox. Run the API contract test with `node --test scripts/runtime-api.test.mjs` after building the backend; ordinary .NET tests use `--filter "Category!=Integration"` and the separate infrastructure job uses `--filter "Category=Integration"`.

**Milestone 1 estimate: 6–9 focused engineering days for one engineer familiar with the repositories**, including review and focused verification: 1–2 days for baseline alignment and defect reproduction, 3–4 days for shared evaluation and executor fixes, and 2–3 days for remaining validation, portal warning/error presentation and CI checks. This assumes the existing local toolchains work and the fixes can be tested with in-memory stores and mock endpoints. It excludes milestone 2 security/versioning and later cloud or connector work. Re-estimate after the initial regression tests if they reveal a larger persistence or API-contract change.

### Milestone 2 implementation progress on 6 October 2026

M2 is implemented locally in the Runtime API and portal, pending review and commit. Draft saves, validation and tests are separate from explicit deployment. Published YAML/script bundles use content hashes, execution history retains stable pipeline/revision identities and snapshots, and legacy filesystem pipelines require explicit adoption without changing their live content. The local catalog uses conditional writes, an interprocess lock, atomic manifests and endpoint collision checks.

Management requests now require configured bearer identities with separate read/write/test/deploy permissions and pipeline scopes. The portal signs in with the same identity and respects those permissions. Scoped host-owned secret grants separate testing from live execution; path traversal and linked script paths are rejected, and management execution cannot access unrestricted host filesystem connectors. The local launcher provisions a loopback-only development token. See [management and deployment](management-and-deployment.md) for configuration, migration, API changes and rollout boundaries. Enterprise OIDC login is not implemented; individual scoped bearer credentials are the initial management identity mechanism.

Verification: 129 pipeline tests (20 added in M2), 21 CLI tests, 118 portal tests, the expanded Runtime API HTTP contract test, full .NET solution build and portal production build pass. Portal lint has no errors and three pre-existing hook warnings. A browser check validated an unsaved snapshot, saved and deployed v1, then saved/tested draft v2 while the deployed endpoint still returned v1. Local regression evidence covers draft/live isolation, stale and concurrent writes, route conflicts, queued and running execution pinning, authentication/authorization, scoped execution history, traversal and secret denial. The cloud deployment catalog is M6 work: the existing Blob/Redis startup registry remains a legacy prototype, not a target for the new portal deploy action. Durable async trigger acceptance and recovery remain M4; the local runtime rejects live async execution rather than pretending to durably accept it. DSL behavior is unchanged.

### Milestone 3 implementation progress on 6 October 2026

M3 is implemented locally across the Runtime API and portal, pending review and commit. Every end-to-end draft test explicitly selects real systems or mocks. A local test session uses the same synchronous executor for sync and async definitions, without changing the saved draft or deployed revision. Strict JSON fixtures identify HTTP pull sources and ordered output destinations, optionally assert outgoing bodies, and cannot fall through to real network or secret access. The portal generates fixture templates from the exact editor snapshot and displays step status, diagnostics, transformed data and connector calls, both immediately and in execution history. Read access to stored inspection data remains scoped to the execution's pipeline.

Verification: 140 pipeline tests, 118 portal tests, the expanded isolated Runtime API contract test, .NET solution build, portal production build and TypeScript checks pass. Portal lint has no errors and three existing warnings. Azure adapter integration tests still require Docker; the Redis round-trip contract now includes test mode and inspection reference. A browser walkthrough verified explicit selection, fixture generation, a recorded body-assertion failure, a successful mock order-to-CRM-to-Emarsys flow and reopening its stored output from history. M3 supports the current HTTP connectors and a supplied JSON trigger; future SQL/RabbitMQ/authentication connectors remain M5. Durable acceptance/retry/replay remains M4. DSL behavior is unchanged. See [local testing](local-testing.md) for the fixture contract and API.

### M3 public HTTP sample pack

The milestone includes four runnable [public HTTP samples](examples/public-http/README.md): Postman order echo, DummyJSON cart creation with delivery, httpbin 503, and a one-second deadline against a delayed response. Each has synthetic input, strict mock fixtures and an expected result. The portal exposes these under **New Pipeline → Start from**, creates independent drafts with distinct endpoints, and loads their input/fixtures in **Test draft** without selecting a mode or deploying. Webhook.site is documented as an optional destination for browser-visible delivery.

Verification now includes 144 pipeline tests and 120 portal tests. All four samples were manually run against their actual public endpoints and their mocks: successful flows completed, and failure flows failed at the intended source. The browser walkthrough verified template creation and both modes. CI uses the deterministic fixtures, with no dependence on public endpoint availability. Webhook.site was not provisioned; its setup is explicit in the sample guide. These examples do not add retry/recovery behavior or alter the DSL.

M3 HTTP inspection now records and displays request method/URL, request and response headers/bodies, status/protocol, start time, duration and effective timeout for real and mock connector calls. Sensitive header values and credential query parameters are redacted before trace persistence. Both successful and rejected responses are retained; transport failures retain request details. Regression coverage includes source/output traces, real/mock persistence, redaction, timeouts and concurrent session isolation (150 pipeline tests), plus the Runtime API transport and stored-inspection contract. Existing execution traces remain readable; newly captured fields require a new test run.

### Milestone 4 implementation progress on 6 October 2026

M4 is implemented locally, pending review and commit. Sync, queued execution and local connector tests share one step engine. The filesystem Runtime API durably accepts async HTTP requests, treats saved state as the scheduling authority, recovers lost queue notifications, and holds an execution claim through external calls and checkpoints. Source responses, prepared output bodies and completed destinations support resume without repeating completed work. Per-source/output YAML chooses automatic or manual retries, with an explicit option for uncertain writes. Attempts retain errors, warnings and HTTP traces. Sync pipelines can author failure bodies/statuses.

The portal shows retries, review states, retention and attempt history. Scoped `recover` permission enables resume with the original revision or a fresh replay using the original or an explicitly selected published revision. Replays retain their origin link and independent recovery artifacts. Default retention is 30 days after terminal completion/failure/review, protects active work and uses restartable deletion. Incoming async HTTP idempotency keys identify retained executions. See [durable processing](durable-processing.md) for schema, API and operational boundaries.

Verification: 170 pipeline tests, 374 Core tests, 21 CLI tests, 7 Parquet tests and 120 portal tests pass. Backend solution and portal production builds pass; final Runtime API build and TypeScript checks pass. Lint has no errors and three existing warnings. Both isolated API contract tests pass, including process kill after an HTTP write, queue loss, retries, permissions and replay across revisions. The process-restart test passed three consecutive runs after a Windows filesystem reader/replacement race was reproduced and fixed. Browser verification exercised resume, attempt history and replay of a failed run with a published correction; it also exposed and fixed duplicate React keys in the new detail controls.

The filesystem adapter is the M4 reference implementation, not a native cloud release. Plain state adapters cannot be used by queued execution without the recovery/ownership contract. Legacy Redis round-trip fields are preserved for sync compatibility, but its TTL, distributed ownership and cloud document cleanup still require M6. Docker-backed Azure tests could not run locally. Existing SSH.NET advisory warnings remain in that dependency graph. No DSL behavior, external RabbitMQ/SQL connector or native cloud installation was added here.

A standalone [AI integration handoff](elwood-live-platform-ai-handoff.md) describes the intended live platform for a separate pet project that will adopt Elwood after release. Its DSL examples and complete HTTP pipeline were verified with synthetic input and strict mocks; the document explicitly distinguishes the target release from current readiness.

### Milestone 5 implementation progress on 6 October 2026

M5 connector implementation is present locally in both repositories, pending review/commit and the broker-backed CI run. HTTP sources/destinations support Basic authentication and OAuth2 client credentials; raw string XML/text is supported. The maintainer confirmed that the scoped SAP flow uses HTTP with Basic authentication. Optional SQL and RabbitMQ packages, Azure Blob reads/writes, authenticated Event Grid validation/notification handling, scoped host-identity grants, connector receipts/metadata and strict mocks are implemented. Existing HTTP inspection remains compatible; the portal now displays SQL/Blob operations and results. See [connector contracts](connectors.md) and the [three representative bundles](examples/connectors/README.md).

The controlled HTTP order chain passes through the Runtime API with OAuth2 CRM and Basic-authenticated downstream delivery. The product sample passes against a real LocalDB procedure and loopback Basic-authenticated HTTP endpoint using a supplied event. Deldo passes against real LocalDB and Azurite: 11 synthetic rows yield five offers/six rejections, the SQL receipt is retained, a missing snapshot container causes failure, and a fresh runner resumes after the container is created without repeating SQL. Conditional Blob reads, create-only/overwrite writes, SQL typed parameters, post-write timeout/error uncertainty, Blob connection loss, Event Grid batch splitting/deduplication/partial acceptance, secret redaction and identity-grant denial have regression coverage. The pet project's target YAML validates directly; offline CLI runs accept valid retry blocks with a note rather than requiring a second definition.

Verification: 171 pipeline, 374 Core, 22 CLI, 7 Parquet and 12 connector non-integration tests pass, along with four local real-service connector tests (SQL, Blob, Deldo recovery and product SQL/HTTP), all three isolated Runtime API contract tests and 120 portal tests. The backend solution and portal production builds pass. Portal lint has no errors and three existing hook warnings; the backend solution retains the existing SSH.NET dependency advisories in its Azure test project. The browser tool refused to reopen the existing error tab, so a visual walkthrough of the new connector inspector is not claimed; API inspection contracts and the compiled portal were checked. Both development servers were restarted with M5.

Acceptance evidence is deliberately bounded: RabbitMQ is unavailable locally and Docker is not installed. The consumer compiles, and CI now provisions SQL Server, Azurite and RabbitMQ for broker acknowledgement/redelivery and the complete RabbitMQ → SQL → HTTP flow. Those two broker tests still need their first run before M5 acceptance can be closed. The pre-existing Docker-backed Azure adapter job also remains unverified here. No live Azure resources or workplace systems were contacted. M6 still owns native hosting, inexpensive Azure adapters and a live Event Grid subscription. DSL behavior is unchanged; the additional pet-project language requests are separate follow-up work.

### Milestone 5 verification closeout — 7 October 2026

The three outstanding verification tasks are complete; this evidence supersedes the pending items in the 6 October entry above.

1. **Real RabbitMQ consumer and product flow: two tests passed.** `RabbitMqIntegrationTests.FailedAcceptanceRedeliversAndAcknowledgementWaitsForDurableAcceptance` verifies redelivery after failed persistence and acknowledgement only after acceptance. `FlowIntegrationTests.RabbitMqProductEventReadsRealSqlAndPostsBasicAuthenticatedHttp` passes through a real queue, an isolated SQL Server LocalDB procedure and a controlled Basic-authenticated HTTP destination. The temporary broker used the official portable RabbitMQ 4.1.8 and Erlang/OTP 27.3.4.18 builds, bound to loopback; no Windows service was installed.
2. **Hosted worker recovery: passed.** `scripts/rabbitmq-runtime.test.mjs` starts a separate Runtime API process against a real broker. It proves that draft-only pipelines do not subscribe; deployed pipelines durably accept and acknowledge while processing workers are paused; the hosted worker reconnects after its broker connection is forcibly closed; accepted work survives an abrupt API process kill and loss of the in-memory scheduling queue; accepted executions retain v1 after v2 is deployed; duplicate messages reuse completed executions without repeating output writes; and a new event runs v2. This tests an actual broker-initiated connection drop and an API process restart, not a broker-machine reboot. CI now exposes RabbitMQ’s management port and runs this test after building the API.
3. **Portal inspection walkthrough: passed.** Created the undeployed `m5-verification-deldo` draft through **New Pipeline → Deldo stock import**, selected **Mocks**, and ran it successfully. Expanded Blob download request/response metadata, the SQL import operation and its receipt (`status: loaded`, `offers_loaded: 5`), and the Blob upload receipt (ETag and size). Reopened execution `0f10e3bd-b72d-4233-998e-a71ae469d197` from history and verified its persisted SQL receipt. This browser check used synthetic mocks; the SQL/Azurite integration tests provide the real connector evidence separately.

Focused commands from the backend root (set the broker/SQL environment variables to dedicated test services):

```powershell
dotnet test dotnet/tests/Elwood.Connectors.Tests --filter "FullyQualifiedName~RabbitMq"
dotnet build dotnet/src/Elwood.Runtime.Api
node --test scripts/rabbitmq-runtime.test.mjs
```

The retained verification baseline is 172 pipeline tests (including the later retention failure-isolation test), 374 Core, 22 CLI, 7 Parquet, 12 connector non-integration tests, six real connector tests, three non-broker API contract tests, the new hosted RabbitMQ API test, and 120 portal tests. The earlier backend/portal builds passed. The existing three portal lint warnings and SSH.NET advisories remain documented. This closeout reran the two previously pending broker tests and added the hosted test; it does not claim a new full-suite or remote CI run. Docker-backed Azure adapter tests still await CI because Docker is unavailable here.

**Review scope:** compare backend baseline `672ef68` and portal baseline `75052c3` with their M1–M5 handoff commits. Review shared evaluation and diagnostics, strict validation, management permissions/path/secret boundaries, draft/deployment isolation, durable checkpoints and uncertain writes, connector contracts/receipts, and portal transport/inspection behavior. The accumulated implementation is being handed off together because the milestones were implemented locally without intermediate commits. DSL engine behavior is unchanged.

**M6 gates remain:** native hosting and distributed storage/ownership adapters, actual Azure event delivery, deployment/security/operations configuration, and enforced default 30-day cleanup for internal data **and delivered output blobs**, with pipeline/destination overrides. Output blob expiry is not implemented by the M5 connector. See [storage retention](storage-retention.md). No live cloud or workplace system was contacted. Obtain independent review before starting M6; use the Eagle/SAP and Deldo sessions as real adoption tests afterward.

### Independent-review blocking fixes — 7 October 2026

The six blocking findings from the review of backend `f69a6e9` / portal `5744a32` are fixed as a dedicated follow-up package, before M6. Endpoint permission metadata replaces request-path suffix classification; production attempt traces retain metadata/sizes rather than body copies; fetched HTTP checkpoints redact credential headers; recovery reads a persistent active-execution index while retention reads the terminal index; permanent RabbitMQ rejection dead-letters without requeue while transient failures reconnect with bounded exponential backoff; and replay enforces the uncertain-write acknowledgement in the backend. The portal sends that acknowledgement and labels omitted/truncated previews.

Regression evidence: 177 pipeline tests, all four Runtime API contract suites and five real RabbitMQ tests passed. The new checks include write-only identities with trailing-slash endpoints, a 1 MiB response with serialized state under 8 KiB and no production test-context document, fetched-checkpoint header redaction, unreadable terminal files during recovery, oversized/missing-ID/conflicting poison messages followed by a valid message, and replay refusal/explicit approval. Portal's existing 120 tests and TypeScript checks passed. Those 120 comprise 112 engine fixture runs and eight gallery/helper/API/sample tests; they are not 120 component tests. Component coverage and the smaller review findings are a separate follow-up package.

The filesystem adapter builds indexes once when upgrading a pre-index store, then maintains them across transitions and process restarts. Index intent is persisted before the state commit so interrupted transitions may leave extra candidates but cannot hide accepted work. Body previews in local tests default to 64 KiB, configurable through `Elwood:Inspection:TestBodyLimitBytes` (0–1 MiB); full processing/checkpoint documents remain separate. DSL behavior is unchanged.

### Preserve and align the baseline

- Keep current local edits intact. Separate the existing portal alignment work from subsequent runtime changes in reviewable commits/PRs.
- Align the portal with DSL 0.7.25 and refresh examples and Monaco language support. For Runtime API script tests (`POST /api/pipelines/{id}/scripts/{name}/test`), read and display `X-Elwood-Warnings`, whose response-header value is a JSON array of strings. In the playground, which uses the TypeScript engine directly, display entries in `result.diagnostics` with `severity: 'warning'`, including on successful results. Neither path must treat warnings as errors.
- Establish current builds and relevant test results for both repositories. Add portal CI and make the separation between ordinary tests and infrastructure integration tests explicit.

### Reproduce and fix execution failures

- Turn the September in-memory probes into regression tests against the current executor code.
- Preserve source/output DSL diagnostics, including errors and warnings, with step identity across all three executors. Persist warnings in execution history on successful steps as well as failed ones. For an `ElwoodResult`, `Success` is false only for `Error` severity; a non-empty `Diagnostics` list is not a failure signal. A failed map, path or request-body expression must not silently fall back to untransformed data or send an unintended request.
- Distinguish a legitimate DSL null result from an evaluation failure and from a missing script. A `.elwood` reference absent from `pipeline.Scripts` must fail the step with a diagnostic naming that reference.
- Persist terminal failure states on early returns and exceptions. Record source/output starts, completion, errors, warnings and useful data references.
- Resolve all supported script-reference locations, including HTTP body and response-status expressions.
- Make validation reject or clearly diagnose unsupported configuration. Preserve documented compatibility aliases deliberately; do not replace silent acceptance with accidental breaking changes.
- Correct the API/portal contract so an execution failure is presented as failed even where the management endpoint successfully returns an execution-result envelope.

**Acceptance scenario:** a mock HTTP order flow posts transformed data to a mock CRM, captures its response and builds the Emarsys request. An invalid transformation stops the affected work, preserves diagnostics, records a failed execution and does not deliver the untransformed payload. A missing/failing source must not leave an execution permanently marked Running. A successful run exposes its steps and results.

**Required regressions:** a step whose evaluation succeeds with a `Warning` is recorded as successful and that warning is persisted with its step identity; a failed step also retains any warnings alongside its errors. A missing `.elwood` script fails with a diagnostic naming the reference, while a legitimate null result remains distinct from either missing-script or evaluation failure. An expression containing `"return "` inside a string literal is evaluated as an expression. Exercise these behaviors across the executor paths and verify both portal warning transports.

Keep this work within orchestration, API integration and portal diagnostics. DSL behavior changes are out of scope; record any newly discovered engine defect separately. Preserve the current production language behavior and recent performance improvements.

## Shared execution behavior

The three existing executors overlap but differ in fan-out, error propagation and persistence. Introduce shared step evaluation and a common execution contract incrementally, covered by behavioral tests. Avoid a wholesale rewrite before the first failure fixes land.

Milestone 1 includes one shared implementation of script-versus-expression detection for the Runtime API and executors. Replace the duplicated `expression.TrimStart().StartsWith("let ") || expression.Contains("\nlet ") || expression.Contains("return ")` heuristic with detection that respects string literals and the existing DSL syntax. A map containing `"return "` in a string literal must not be classified as a script; cover that case with a regression test. The shared evaluation contract must retain the value, errors and warnings and distinguish a missing script from an evaluation failure or a legitimate null result. This is caller-side integration work, with no change to engine semantics.

Local synchronous testing and distributed asynchronous hosting must use that contract. Execution strategy determines where and when steps run; it must not change the pipeline's transformations or dependency rules. An async definition does not need a synthetic production HTTP response merely to run in the local test harness.

## Versioning and deployment

Publish an immutable bundle of YAML and referenced scripts, and have the active deployment point to one bundle. Each execution stores that revision identity. Saving or validating a draft must not update the active route or bundle. A local test identifies the exact draft snapshot it ran.

Deploying a new version affects subsequent triggers. Existing executions and resumed attempts retain the original version. A fresh replay defaults to that version and allows explicit selection of a newer one. Handle existing filesystem pipelines with an explicit migration path so introducing drafts does not silently deactivate or republish them.

Milestone 2 also owns the management security gate: authenticate management API requests, authorize draft/test/deploy and execution-management actions, contain pipeline/script filesystem access within the configured roots, and scope secret access to the authorized pipeline and operation. Before shared or externally reachable use, automated checks must demonstrate that unauthenticated or unauthorized management actions, path traversal and out-of-scope secret requests are rejected. The portal must use the same management identity and permissions; UI restrictions alone do not satisfy this gate.

## Durable execution and retention

Acceptance must persist the input, selected revision and recoverable scheduling intent before acknowledging an external event. Provider adapters must implement recovery across failures between those operations. Do not assume writing the payload and then publishing a queue message is sufficient.

Replace shared-document read/merge/write races with a defined assembly strategy, such as immutable per-step results merged at dependency boundaries. Establish atomic step claiming and attempt identity. Design retry behavior for uncertain external-write outcomes; a queue cannot by itself guarantee that a remote CRM write happened only once.

Implement maintainer-controlled retry policies for sources and outputs, then expose resume and fresh replay as distinct operations. Retain inputs, intermediate results, history and referenced versions for 30 days by default, configurable per pipeline. Define consistent expiry across stores without deleting artifacts still needed by an active execution.

## Connectors and cloud portability

External RabbitMQ ingestion is separate from Elwood's internal scheduling queue. SQL Server stored-procedure calls need explicit parameter mapping and result-set representation. HTTP steps need response capture and appropriate authentication; establish the actual OAuth2 flow where required. The confirmed M5 SAP PI/PO interface is HTTP with Basic authentication. Broader SOAP/TQL-specific support needs concrete contracts before specialized implementation.

### Milestone 5: two equally required integration families

On 6 October 2026 the maintainer confirmed that the original workplace-derived integrations and the pet project's Deldo import remain **joint M5 priorities**. Deldo does not replace or defer the original scope. M5 is complete only when both families have evidence; cloud installation and measured operating cost remain M6. See the [Deldo adoption review](gomme-adoption-review.md) for reproduced results, contract gaps and feedback for the pet project.

| Acceptance flow | Required behavior |
|---|---|
| HTTP order → CRM → Emarsys | Basic/OAuth2 as required, ordered calls, CRM response used in the next payload, authored response and retry policy, protected credential handling |
| External RabbitMQ → SQL stored procedure → SAP endpoint | Persist before broker acknowledgement, handle redelivery and reconnects, bind procedure parameters, map result rows with the event, deliver through the agreed PI/PO interface |
| Deldo BlobCreated → Blob read → SQL import + Blob snapshot | Accept each event durably, read the intended file version, normalize CSV, pass the whole JSON output to a procedure, capture its receipt, write a version-specific snapshot and recover each destination independently |

Implement in reviewable packages, sharing work between the two families:

1. **Connector contracts and inspection.** Replace HTTP-only assumptions about responses, operation side effects, failures and uncertain outcomes with a shared connector contract. Preserve existing HTTP traces. Define source metadata, destination receipts, cancellation/deadlines, redaction, expression/secret resolution and result-size limits. Extend strict real/mock testing and portal inspection for every new connector. A stored procedure used as a source can write data; its risk must not be inferred from an absent HTTP method.
2. **SQL in both directions.** Add an optional SQL Server connector package with typed, parameterized stored-procedure inputs and a documented result-set contract. Include a destination that binds the complete mapped output as one `nvarchar(max)` parameter (proposed default `@payload`) and records the first result set. Capture database errors and distinguish definitely-not-sent failures from uncertain writes. Apply the pipeline's M4 retry/approval policy; do not silently retry ambiguous commands inside the connector. Test the product lookup and Deldo import paths together.
3. **Blob read and write.** Add optional Azure connectors with scoped credentials or a host-authorized managed identity, safe field resolution, text/JSON reads, JSON snapshot writes and metadata capture. Support a conditional ETag read and explicit blob-version selection where available; a stale event must not silently read replacement bytes. Define overwrite/precondition behavior and timeout recovery. Do not advertise binary/Parquet delivery by merely setting a MIME type; unsupported serialization remains a validation error.
4. **Ingress and HTTP completion.** Implement external RabbitMQ acceptance/reconnect/redelivery and an explicitly enabled Event Grid webhook adapter with subscription validation, authenticated notifications, bounded batch handling and per-event deduplication. Preserve M4's persist-before-acknowledge rule. Complete HTTP authentication and the agreed SAP transport. Keep provider ingress adapters separate from the internal execution queue. Event Grid → Service Bus can be an M6 hosting alternative once its actual message envelope is tested; identical source scripts across transports must be proved, not assumed.
5. **Acceptance and documentation.** Run all three flows with deterministic mocks and controlled real systems. Exercise concurrent duplicate ingress, version mismatch, a write followed by connection loss, recovery after SQL succeeds but a snapshot fails, and repeated broker delivery. Verify receipts, warnings and redacted details in the portal. Update the YAML reference and sample bundles to the shipped schema; then the Deldo target bundle must validate directly without its temporary HTTP twin.

The desired ordinary callable-lambda enhancement is a separate DSL follow-up, with .NET/TypeScript parity tests if implemented. It is not a prerequisite for M5: the current Deldo script runs without it.

### Milestone 6: native hosting and the small Azure profile

**First-release gate added on 6 October 2026:** the maintainer confirmed 30-day default retention for **internal execution artifacts and delivered output blobs**, configurable per pipeline/destination. This is mandatory for every supported provider. Implement and verify cleanup plus lifecycle safeguards for current objects, previous versions/snapshots, orphans and temporary uploads, with drift/failure visibility and bounded growth. Source-owned files are excluded. Output expiry must still run after execution-history expiry or pipeline deletion. See the full [storage retention contract](storage-retention.md), including ownership, finite active-work limits, provider differences and acceptance tests. The existing `ttlSeconds` cleanup does not yet enforce destination retention; M6 cannot close with merely documented policies or expiry metadata.

Include a small Azure profile for the pet project alongside the workplace-oriented Azure/Service Bus installation. The pet project's stated budget is **less than €15/month for the whole application**, not an Elwood-only allowance. Avoid making Redis a compulsory service. Evaluate native Functions hosting with Table state/catalog indexes, Blob documents/bundles and Storage Queue work notifications; compare Service Bus when its features are needed. These are implementation candidates, not adapters already supplied or a price guarantee.

The smaller profile must satisfy the same durable-acceptance, execution ownership, stale-worker fencing, revision pinning and retention requirements as M4. Design bounded queries for due work and recovery rather than translating the local one-second full-state scan into cloud storage polling. Keep large payloads and traces in documents. Prove crash recovery, conditional writes, lease loss, duplicate delivery and consistent deletion against the selected services.

Produce deployment automation, an explicit supported runtime/region configuration, workload limits, and a measured cost model covering the portal/API, database, storage, events, secrets, monitoring and data transfer. Free grants alone are not acceptance evidence. Use the workload inputs now recorded in [product direction](product-direction.md#pet-project-hosting-inputs-for-m6) before claiming the budget is achievable. Cloud deployment and a live Event Grid subscription are M6 evidence; M5 verifies the connector and ingress behavior locally/against controlled services.

Use common adapter acceptance tests for execution state, documents, work scheduling and secrets. The provider-neutral core must not require Azure services. Given the existing Azure code and available workplace environment, completing Azure first is a proposed delivery sequence, not a restriction of the product. Select the second provider before committing to a broad cloud-support claim, and verify portability by exercising the same pipeline behavior on that native deployment.

Each supported installation needs deployment automation, configuration/secrets guidance, management identity integration, observability, upgrade and rollback procedures, and a tested recovery path. Containers may assist local tests but are not a substitute for the agreed native/serverless hosting model.

## Decisions that can wait until their milestone

- Provider packages and internal brokers included in the first public release.
- Enterprise identity-provider integration beyond the scoped bearer identities implemented in milestone 2.
- Optional per-connector mixed testing (M3 implements explicit whole-run real/mock selection).
- Specialized SOAP/TQL interfaces beyond M5 HTTP Basic; SQL result and RabbitMQ subscription contracts are documented in [connectors](connectors.md).
- Provider-specific load targets and operational acceptance criteria (M4 defines retry, failure-response and retention semantics).

These decisions do not block regression tests and fixes for swallowed transformation errors, incorrect execution state or misleading portal results.


### Review follow-up: storage and management hardening

A separate work package after the six blocking fixes adds removable cross-process execution claims, old temporary/access-lock cleanup, replace-compatible readers and bounded Windows rename retries. Invalid legacy folder IDs are skipped per pipeline with a log entry. Management denials have a dedicated exception type; filesystem failures return a generic 500. Proxy configuration rejects local development credentials, Basic trigger authentication compares hashes in constant time, and management scopes compare case-insensitively.

Provider HTTP statuses now obey the pipeline's status allowlist; interrupted terminal steps have completion timestamps. Synchronous execution loads the immutable pipeline once and waits for the next retry deadline. The classifier reads only the first lexer token, with no DSL semantic changes. Offline CLI binding/snapshot differences are explicitly documented in local-testing.md. UTF-8 trace previews stop on a character boundary. API tests use a longer cold-start budget and exercise proxy rejection, generic storage failures and scope casing.


### Review follow-up: connector cancellation and credential reuse

A separate connector package propagates caller cancellation from host shutdown through every connector, preserves interrupted durable write checkpoints for recovery, and expires canceled non-durable tests. Blob credentials are reused; OAuth2 tokens use a bounded, expiry-aware cache scoped to the HTTP client and resolved credentials/scope. Regression coverage includes concurrent refresh suppression, rotation, scope separation, expiry, uncached failures, cancellation before the HTTP timeout and restart classification of an interrupted write. Verified: 187 pipeline tests and 16 non-integration connector tests.


### Independent-review closeout — 7 October 2026

All six blocking findings and the smaller listed runtime fixes are addressed before M6. The final edge-case regressions cover provider status zero as a transport fault, empty/null legacy definitions, active temporary files during cleanup, and abandoned recovery-index entries without removing a writer's acceptance intent. Documentation now removes unsupported Service Bus/email YAML examples and destination-level retry keys, documents offline CLI differences and reverse-proxy requirements, removes the machine-specific pet-project path, labels the disposable CI database password and gives API cold starts a 60-second health-check budget.

Reviewable work packages:

- Backend `99cfc77`: the six blockers and regression coverage.
- Backend `3e4b29e`: storage lifecycle, management boundaries and execution efficiency.
- Backend `468f9e7`: connector cancellation and credential caching.
- A final backend closeout commit: edge cases, reference corrections and CI/test startup resilience.
- Portal `b4c147e`: replay acknowledgement and bounded-body inspection labels.
- Portal `0e7d57b`: actual editor/run-panel component coverage, CI wiring and case-insensitive management scopes.

Verification during the follow-up:

| Area | Evidence |
|---|---|
| Pipeline | 190 tests pass; the 17 review regressions also pass after final index cleanup changes |
| DSL | 374 Core tests pass; no syntax or evaluation behavior changed |
| Connectors | 16 non-integration tests pass, including OAuth reuse/expiry/rotation and uncached failure |
| API | Three non-broker scripts pass (four tests including proxy startup rejection); isolated API build has no warnings/errors |
| RabbitMQ | All five real broker tests and the hosted restart/reconnect script passed during the blocking-fix package; the portable broker/Erlang processes and downloaded files were then removed |
| Portal | 120 existing tests (112 engine fixtures + eight other checks), six new component tests, TypeScript and production build pass; lint has no errors and three existing hook warnings |

The component tests render the actual editor and run panel with mocked Monaco and network boundaries. They exercise Validate without saving, both explicitly selected real/mock modes against the exact unsaved snapshot, Save draft before Deploy with the returned revision, conflict display and read-only permissions. No new visual browser walkthrough is claimed. Docker-backed Azure adapter tests and remote CI have not been run locally in this follow-up. The unrelated Parquet fixture change from the independent review is left outside these commits.

A separate dependency audit during test-tool setup reports existing portal application advisories, including critical Next.js 16.2.3 and high Axios findings. These require a dependency-upgrade/verification package before external hosting; they are not fixed by adding component tests. Previously recorded SSH.NET advisories also remain outside this review-fix scope. M6 implementation, deployment and destination-blob retention have not started. No live cloud/workplace resources were contacted, and no commits were pushed.


### Second independent-review follow-up — 7 October 2026

The six residual requests are implemented in a separate package: a configurable 60-second sync budget linked to request cancellation, persisted deadlines and conservative interrupted-write recovery; logged per-file filesystem/index fault isolation; bounded/redacted HTTP rejection errors; per-identity lazy OAuth token requests; Core 0.7.26 packaging; and portal Next 16.4.0/Axios 1.20.0 upgrades.

Smaller corrections include a denying default secret scope for recovery, macOS errno 35 lock handling, invalid artifact-name tolerance, an acknowledged replay that actually completes in both unit and API tests, a separate replay uncertainty confirmation, an accurately named generic deploy-error component test, and temporary Parquet fixture generation. The YAML reference now describes whole-selection output evaluation and explicitly separates unavailable destinations. Offline CLI `$slice` and isolated contract DLL selection are documented.

Verification: 197 pipeline, 374 Core, 22 CLI, 7 Parquet and 17 non-integration connector tests; five API contract tests against an isolated build; portal 120 node tests, eight component tests, TypeScript and production build. Lint has no errors and the same three existing dependency warnings. Packaging builds net8.0/net10.0 and its nuspec requires Core 0.7.26 for both. No DSL behavior changed. Broker/cloud integration suites and remote CI were not rerun in this package.

The portal production audit after compatible transitive updates has no critical/high/moderate findings and two low findings through Monaco/DOMPurify. The full developer audit also has five high findings in the ESLint → fast-glob → micromatch → braces chain; npm's proposed downgrade to eslint-config-next 14 is not applied. These residual advisories remain tracked, not claimed fixed.

M6 input: bound synchronous work below the chosen provider gateway deadline; retain per-record fault isolation in cloud adapters; use bounded due/expiry queries and storage-enforced fencing. Azure resource creation requires the owner's approval of the proposed isolated resource group and costs. Subscription access was checked read-only; no cloud resources were created by this follow-up.

### M6 storage lab verification — 8 October 2026

The maintainer approved temporary `elwood-m6-rg` with a €10 total allowance; exceeding it requires approval. The storage-only lab is now deployed in West Europe. Three live Blob probes pass: takeover after lease break, takeover after natural expiry, and ETag-safe/version-specific cleanup. A real permission gap was found and fixed: deleting previous versions requires `deleteBlobVersion/action`, absent from Blob Data Contributor. The lab now uses account-scoped Blob Data Owner, plus Queue/Table Data Contributor. All synthetic live blobs/versions were cleaned up; 100 bytes remain soft-deleted under the seven-day lab policy. The billing query has no data yet.

This is service and infrastructure evidence, not M6 completion. Durable cloud adapters, hosted management/catalog parity, Functions hosting, production retention and end-to-end native integration verification remain. See [the Azure profile](m6-azure-profile.md#live-lab-verification--8-october-2026) for exact results and limits. No production resources were touched.

### M6 native-host closeout — 8 October 2026

The implementation now includes leased Blob checkpoints, immutable execution documents, bounded Table indexes, Blob CAS deployment catalog, native Functions HTTP/management/queue/recovery/retention, Storage Queue and Service Bus transports, authenticated Event Grid delivery and an optional always-running RabbitMQ ingress. Delivered-output ownership/expiry survives history deletion, with bounded admission, finite execution lifetime, installed lifecycle checks and reserved terminal/deletion capacity. No DSL behavior changed.

The independent review here is complete and its findings are fixed, including the final generation-fencing race. A separate review by the owner's other AI session is still requested. See [review evidence](m6-release-evidence.md), [installation](../infra/azure-small/README.md) and [operations/capacity](azure-operations.md); the master checklist remains the authoritative closeout status. Earlier paragraphs are historical baselines, not current cloud-support claims.

The native small profile has explicit low limits and is not certified for the full Deldo workload (up to 25 MiB CSV expanding beyond 32 MiB, 1,000 retained executions/month). Large-file/capacity acceptance and the newly confirmed [content-hash suppression](content-deduplication.md) remain prerequisites before that adoption. Hash history defaults to 30 days, is configurable per pipeline and supports explicit replay override. The synthetic hosted families verify correctness; real Eagle/SAP and Deldo adoption still needs its own acceptance evidence.
