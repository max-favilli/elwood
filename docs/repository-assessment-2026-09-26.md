# Repository assessment — 26 September 2026

Backend baseline: `main` at `5a984f3`, cloned from `https://github.com/max-favilli/elwood.git`. Portal baseline: `main` at `75052c3`, cloned from `https://github.com/max-favilli/elwood-portal.git`. Both repositories now live under the shared workspace `D:\git-repos\elwood`, in sibling directories `elwood` and `elwood-portal`. This is an initial source and test review, not a cloud deployment or exhaustive audit.

## Overall assessment

The transformation DSL is established and actively maintained. The package version is 0.7.21; the latest engine changes on 23 September reduce copying in grouping and object construction. The integration platform is substantially beyond a skeleton: it has tested pipeline execution, persistence abstractions, a management API, and an initial Azure synchronous runtime. Its main unfinished work is connecting and hardening these components into an operable cloud product.

The management UI is the separate repository `max-favilli/elwood-portal`, now also inspected. The React application in the backend repository is the DSL playground, not the management portal. The portal has progressed further than the backend roadmap records.

## Architecture and implemented capabilities

| Area | Implementation and status |
|---|---|
| DSL reference engine | `dotnet/src/Elwood.Core`: lexer, parser, AST, tree-walking evaluator, diagnostics, bindings, extension methods, lazy values. |
| JSON integration | `Elwood.Json` adapts System.Text.Json through value/factory abstractions. `Elwood.Newtonsoft` currently contains a project file but no adapter implementation. |
| TypeScript DSL | `ts/src`: independent lexer/parser/evaluator with shared conformance cases; ESM package. Uses eager arrays rather than .NET's lazy streaming. |
| Formats | CSV/XML/text support plus separate XLSX and .NET Parquet packages. TS Parquet remains unfinished: its handler returns before its asynchronous reader completes, as acknowledged in known issues. |
| Embedding and tooling | NuGet/npm packaging, CLI, transformation HTTP API (`Elwood.Api`), Monaco playground, CI/release workflows. |
| Pipeline model | YAML orchestration plus `.elwood` transformation scripts; source dependency stages, source envelopes, intermediate data model (IDM), output maps and fan-out. |
| Execution | `PipelineExecutor`, `SyncExecutor`, and `AsyncExecutor`. HTTP and file source/destination connectors are implemented. |
| Persistence | In-memory/filesystem stores; Git-backed pipeline revisions; Redis execution state and registry; Blob documents and pipeline storage. |
| Management backend | `Elwood.Runtime.Api`: pipeline/script CRUD, validation, script tests, execution APIs, metrics, local HTTP triggers. Defaults to filesystem pipeline/state storage. |
| Azure runtime | HTTP-triggered Function reads pipeline content through Redis and executes synchronously. Terraform provisions storage, Redis and a Function App. |

The intended data flow is: trigger or pulled data → source transformations → IDM → output transformations → destinations. The DSL remains a separable library underneath orchestration.

## Highest-priority gaps

1. **Cloud async execution is not wired up.** Only `InMemoryStepQueue` implements `IStepQueue`; there is no Service Bus adapter or queue-trigger Function. The HTTP Function constructs `SyncExecutor` directly rather than dispatching on pipeline mode. Terraform has no Service Bus resources.
2. **Async reliability needs focused work before queue deployment.** Source workers read, merge and overwrite the same IDM document, so parallel sources can overwrite one another's changes. Duplicate suppression checks whether a step is already completed but does not atomically claim it; overlapping deliveries can both proceed. Source failures record a failed step and return without completing the execution. These findings are from code inspection; concurrency/failure reproductions were not run in this assessment.
3. **Management access control is unfinished.** The management API has no authentication/authorization middleware and permits any CORS origin. Optional Basic authentication on pipeline triggers does not protect management operations. Pipeline/script paths also need containment validation: stores and temporary-file preparation use supplied identifiers/script keys in `Path.Combine` and file writes.
4. **Authoring-to-deployment integration is incomplete.** Git and Blob stores exist, but the management API is wired to the filesystem store. Deployment/restore endpoints, pipeline publication, and a verified portal-to-cloud workflow remain to be completed.
5. **Cloud validation has a coverage gap.** The CI test command filters out `Category=Integration`, so the Redis/Azurite tests are not executed by that job despite comments suggesting otherwise. No Azure deployment was verified here.
6. **Operational features remain partial.** Cloud HTTP triggering currently accepts POST; broader method and parameterized route support, cancellation, document inspection, secret scopes and Key Vault references are unfinished.

## Documentation and history

- README still describes 68 shared cases; the TS runner currently discovers 110 executable conformance cases.
- Roadmap entries mark Git/Redis work incomplete in one section and complete later. Blob storage, Terraform and Functions code exist despite unchecked corresponding tasks.
- `known-issues.md` still says 97 .NET tests and proposes lazy TS evaluation, whereas the roadmap later explains the choice to retain eager arrays.
- `origin/feat-azure-deployment` has divergent commit history, but its Azure Functions and Terraform files match `main` in a direct tree comparison. Do not assume it contains missing cloud work simply because its commits appear unmerged; newer DSL work is on `main`.
- No tracked `AGENTS.md` or `CLAUDE.md` was found. Historical roadmap references to Claude instructions and portal prompts do not provide those files in this checkout.

## Verification

- .NET non-integration suite: **256 passed** — Core 168, Pipeline 60, CLI 21, Parquet 7.
- TypeScript: **183 passed** across six test files, including 110 shared conformance cases plus discovery validation.
- TypeScript compiler build: **passed**.
- Full .NET solution build, including the Azure Functions host: **passed**, with the two dependency warnings below.
- Portal production build, including TypeScript checking: **passed**.
- Portal lint: **failed with 12 errors and 5 warnings** (explicit `any` types, hook dependencies and unused variables); these are baseline findings, not introduced changes.
- Azure integration suite: **not run**; Docker command was unavailable locally. These tests were explicitly excluded from the .NET run.
- NuGet restore reported two high-severity advisories for transitive `SSH.NET` 2023.0.0 in the Azure integration-test project. Dependency cleanup is warranted; this output alone does not establish exposure in deployed runtime packages.
- The Parquet test regenerated a tracked fixture; it was restored after testing. No implementation changes were made.

## Suggested resumption sequence

1. Inspect `elwood-portal` and establish one local end-to-end integration using the existing sync runtime, including an execution result visible in the portal.
2. Define the first cloud milestone around that concrete integration; keep DSL changes limited to demonstrated needs and regressions.
3. Complete authentication, path containment, secret handling and the authoring/deployment path for that milestone.
4. If durable async execution is needed, first address parallel IDM writes, step claiming, crash recovery and failure propagation, then connect Service Bus and its worker Function.
5. Add dedicated cloud integration CI and deploy the representative integration to a development Azure environment. Update the roadmap to reflect verified behavior.

The most useful immediate next step is choosing a single integration to carry through authoring, deployment, execution and monitoring.

## Portal findings

The portal's latest commit is from 12 April 2026. It uses Next.js 16, React 19, Tailwind and Monaco. Source review confirms pipeline list/create/edit screens, a file-tree script editor, validation, a run panel with direct execution and HTTP trigger modes, execution list/detail inspection, test-versus-real filters, and an embedded transformation playground. API calls target the actual runtime, defaulting to localhost:5000 through `NEXT_PUBLIC_API_URL`.

Concrete gaps:

- The portal locks `@elwood-lang/core` to **0.3.0**, whereas the backend repository is **0.7.21**. Its declared `^0.3.0` range does not include 0.7.x. Browser transformations can therefore differ from current backend behavior.
- `start-dev.ps1` hardcodes the previous machine's `C:\GitAzureDevOpsRepos` paths. It needs repository-relative paths for this workspace. The shell counterpart assumes the sibling name `Elwood`, which differs in case on case-sensitive systems.
- The dashboard is a landing page with a runtime-health placeholder. The sidebar links to `/admin`, but no corresponding route exists. Authentication, deployment UI and revision restoration are not implemented in the inspected routes.
- The API client has stale contracts: `triggerExecution` sends `input` while the backend expects `payload`; the newer `executePipeline` helper used by the run panel sends the correct field. `ExecutionState` omits `isTest`, so the execution page accesses it through casts.
- No automated portal test suite or test script was found. The successful production build establishes compilation, not end-to-end behavior.
- A test execution sets an execution-state flag; `SyncExecutor` still performs its normal work. The flag is not a dry-run guarantee.

For joint development, prioritize the portable launcher and DSL version alignment, then a representative end-to-end run, followed by access control and cloud deployment. Preserve each repository's independent Git history.

## Follow-up review after portal alignment

The portal now pins DSL 0.7.21, has a working shared development launcher, and includes 110 refreshed examples. Its 111 checks comprise 108 expected-output checks, two explicit crypto-runtime limitation checks, and a gallery coverage check. The production build passes. Both local servers have been started and health-checked. Remaining portal lint findings are nine errors and five warnings outside the changed playground/scripts.

This follow-up is deeper source analysis with isolated runtime probes. It is not a complete production certification: Azure deployment, load/failure recovery, external integrations, browser workflow coverage, and security testing remain unverified.

### Reproduced runtime defects

These probes loaded the existing .NET test-build assemblies into PowerShell and used only in-memory state. No existing pipeline, live connector, or external destination was invoked.

1. A sync pipeline with input `{"value":42}` and output map `$.doesNotExist.toUpper()` returned `IsSuccess=true`, zero errors, and the original `value:42` in its output. `SyncExecutor.EvaluateReference` discards unsuccessful DSL results by returning null; callers then retain the previous data. Async execution uses the same pattern. A map failure must not become a successful delivery of untransformed data.
2. A sync source with no usable trigger/pull configuration returned a failed result, but its stored execution remained `Running`. Early failure paths bypass terminal-state persistence.
3. The completed sync run contained zero recorded source steps and zero output steps. The API and cloud HTTP Function use this executor, so the portal's step-detail UI is ahead of the data this execution path records. Its document-store field is assigned but unused.

### Further source findings

- Three executors implement overlapping behavior independently. `PipelineExecutor` implements source-path fan-out; `SyncExecutor` and `AsyncExecutor` do not apply `source.Path`. Concurrency properties are not a reliable promise of concurrent execution. Tests proving that steps were queued together do not prove that simultaneous workers merge safely.
- Async workers overwrite one shared IDM document after reading it. Completed-state duplicate checks are not atomic execution claims. Crash windows also exist between storing completion and scheduling the next step. These need deterministic concurrency/crash tests before queue infrastructure is connected.
- The YAML parser ignores unmatched properties. The reference documentation advertises connectors and retry policies absent from execution, and uses destination names such as `restEndpoint` while the typed YAML schema expects `http`. Unsupported configuration can therefore be silently ignored instead of rejected.
- Runtime output serialization remains JSON-oriented even when another content type is declared. Format functions in the DSL are more complete than end-to-end pipeline format delivery.
- The parser resolves source-map and some output script files but does not load HTTP body script references or response-status script references in its current traversal.
- Portal Validate saves to the active store before validation. Run executes the stored pipeline by ID, so unsaved editor content can differ from the executed version. Direct execution returns HTTP 200 even for a pipeline failure, and the run panel's success styling relies on HTTP status rather than the response's `success` field.
- A test run is a history label, not an isolation boundary: it can contact real systems and write destinations. Its desired semantics need a product decision.
- Management endpoints have no authentication/authorization; path containment, author permissions, secret access, and outbound connectivity controls need design appropriate to the deployment model. Trigger Basic auth is not management authorization.
- Cloud hosting currently covers the execution Function rather than a complete portal/management/deployment system. The management API still uses filesystem storage; publication, revision selection, rollback and cache consistency are unfinished.
- CI excludes the Azure integration tests; the portal has no checked-in CI workflow. Cross-executor behavior, failure-state persistence, concurrent workers, and complete browser-to-runtime flows need coverage.

### Proposed completion gates

1. **Trustworthy local execution:** reject unsupported YAML; preserve DSL diagnostics; agree on executor semantics; persist all terminal states and steps; distinguish saved/draft/live/test behavior; prove the chosen integration's successful and failing paths.
2. **Controlled team use:** management login/roles, validated paths, scoped secrets, explicit publication, immutable revision per execution, rollback, and useful payload/error inspection.
3. **First cloud deployment:** a reproducible dev environment covering portal, management API, execution runtime and storage; automated deployment and integration checks; retention, logs and actionable failure monitoring.
4. **Durable async, if required by the first use case:** race-safe IDM assembly, step claiming, destination idempotency, retry/backoff/dead-letter policy, recovery, then Service Bus adapter and worker.

Do not equate completing the platform with implementing the entire aspirational roadmap. AWS hosting, a connector marketplace, a visual designer, LSP and Elwood DB can remain separate initiatives unless required by the first users.

Open product decisions: first adopter/deployment model; first integration and operating scale; Azure constraints; required triggers/connectors; production DSL compatibility commitments; acceptable retry/duplicate semantics; test isolation; deployment approval model; retention/privacy and support ownership.

### Product-direction clarification

The maintainer subsequently confirmed that the primary product is an open-source, self-hosted integration platform for any organization, with hyperscaler choice and avoidance of vendor lock-in. A hosted commercial offering is secondary. Initial use cases include HTTP-based e-commerce order integrations to CRM/SAP/Emarsys, followed by Centric/SAP product synchronization involving RabbitMQ, HTTP, SOAP and TQL. See [product-direction.md](product-direction.md) for confirmed intent and remaining decisions. Azure/Service Bus-first suggestions above are provisional and must not be read as an agreed product constraint.

On 27 September, the maintainer confirmed developer-oriented YAML/DSL authoring and provider-native/serverless deployments. The order example requires an intermediate CRM POST whose response feeds the subsequent Emarsys transformation. The detailed product example uses an external RabbitMQ trigger, a SQL Server stored-procedure pull, message/result merging and delivery to SAP PI/PO. External brokers are independent of the installation's internal broker (for example, RabbitMQ ingestion with Azure Service Bus scheduling). These examples illustrate general capabilities rather than limiting the platform to those integrations. SQL pull configuration/connectors and RabbitMQ trigger hosting are additional concrete gaps in the current implementation.

Further confirmed requirements: source/output retry and caller failure responses are pipeline-controlled; async triggers acknowledge only after durable acceptance; executions remain version-pinned; operators can resume a failed step or replay from the original message, defaulting to the original version with explicit selection of a newer version for fresh replay. Retention defaults to 30 days and is configurable per pipeline. Authoring follows save-as-draft, validation, local synchronous testing (also for async definitions), then explicit deployment. The existing three-day TTL, save-as-live behavior and independent executors must be reconciled with these requirements. See the product-direction document for current decisions rather than older provisional recommendations in this assessment.

Local tests must support both real systems and mocks, explicitly selected by the maintainer for the test run. The current `isTest` history flag is insufficient: the runtime must actually honor the chosen connector behavior, and the portal must expose that choice.
