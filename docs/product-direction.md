# Product direction

Recorded from the maintainer's answers on 26–27 September 2026. This document separates confirmed intent from implementation proposals and unresolved decisions.

## Confirmed intent

- Offer an open-source integration platform that any company can install and operate itself.
- Position it as an alternative to MuleSoft, SnapLogic and Boomi, with freedom from vendor lock-in as a central objective.
- Let adopting organizations choose their hyperscaler. Azure-specific dependencies must not become mandatory dependencies of the overall product.
- The maintainer's workplace will adopt it and provide early real-world integrations, but that workplace is not the product's exclusive target.
- A hosted trial/service and possible commercial offering are attractive, but secondary to the self-hosted product.
- Initial authors are developers comfortable with YAML and Elwood scripts. A strong code-based editor is sufficient for the first release; a visual designer is not required.
- Hyperscaler choice means native/serverless deployments using each provider's services, not merely running a common container stack on different clouds. The providers required at first release are not yet specified.

## Initial integration families

1. E-commerce orders flowing to CRM, SAP and Emarsys, primarily through HTTP-based integrations.
2. Product synchronization between Centric and SAP, involving RabbitMQ messages, HTTP, SOAP and TQL.
3. Supplier stock/price imports for the pet project (`gommenew`): Deldo BlobCreated events, CSV from Blob storage, normalization to a shared offer contract, SQL Server import and a Blob snapshot.

These are representative integration families, not the exclusive product scope or a fixed list of launch integrations. Exact products/versions, payloads, volumes and delivery guarantees remain to be clarified. The maintainer confirmed on 6 October 2026 that the specific M5 SAP PI/PO destination is HTTP with Basic authentication. Both REST and SOAP/XML matter to the broader product; TQL and specialized SAP protocols still need concrete contracts.

The first two families come from the workplace; the third was added from the pet project's handoff on 6 October 2026. The maintainer confirmed that **both the original integrations and Deldo stay M5 priorities**. Passing only one family is insufficient to complete that milestone. The pet project will adopt the platform after release. Its requested Azure profile must be evaluated within a total application budget below €15/month; this is an M6 hosting requirement to validate, not a promised operating price. Redis must not be a mandatory dependency of that small installation. See the [adoption review](gomme-adoption-review.md) and [implementation plan](implementation-plan.md).

### HTTP order example — clarified

1. A deployed pipeline declares an HTTP trigger endpoint. Elwood makes that route available to callers.
2. The e-commerce application POSTs an order as a JSON request body.
3. Elwood transforms the order into the CRM schema and POSTs it to the CRM API.
4. Elwood retains the CRM response, merges selected response data with the original order, and transforms the result into the Emarsys schema.
5. Elwood sends that result to Emarsys through an HTTP API call, after the CRM call.

CRM authentication is OAuth2 or Basic; the maintainer has not yet confirmed which this particular integration uses. Both synchronous and asynchronous HTTP flows remain part of the design. For synchronous flows, the pipeline maintainer defines the response body and status, including failure responses; which mode this specific example uses remains open.

An intermediate HTTP call can write to a system and return data required downstream. It must therefore participate in dependency ordering and data/state capture, rather than be modeled only as a terminal output delivery. The existing HTTP pull-source configuration supports POST bodies and responses, but its reliability and script resolution need the fixes identified in the assessment.

### Message-driven product example — clarified

1. A third-party system publishes a message to its RabbitMQ instance.
2. The pipeline declares a message/event trigger with RabbitMQ connection and subscription configuration.
3. That event starts an execution, which pulls data from Microsoft SQL Server by calling a configured stored procedure.
4. Elwood combines the message JSON with the stored procedure's tabular result and transforms the combined data.
5. The output is delivered to SAP through an endpoint exposed by SAP PI/PO.

SQL connection configuration, stored-procedure name and parameter mapping belong to the pull-source contract. Async message acceptance must be durable, as specified below. The exact message subscription, result-set mapping and PI/PO request protocol still need definition.

### External brokers versus internal execution transport — confirmed

- RabbitMQ in this example is an external integration system. It does not dictate Elwood's internal queue technology.
- The adopting organization chooses an internal broker supported by its chosen deployment. At the maintainer's workplace, the intended combination is Azure with Azure Service Bus internally, while consuming third-party RabbitMQ events externally.
- External trigger connectors and internal execution-queue adapters are separate responsibilities. Pipeline business logic should not need rewriting when the internal broker changes.

## Confirmed execution behavior

### Pipeline-owned retry and response policies

- The YAML pipeline maintainer decides whether a source or output uses a retry policy. Retrying is not an unconditional global behavior.
- The maintainer controls what is returned to the HTTP caller when a source fails. Synchronous pipelines must support authored response bodies and status codes for successful and failed outcomes.
- Exact retry syntax, defaults, backoff, eligible errors, dependency behavior on failure, and response-expression bindings are still to be designed. No hardcoded CRM/Emarsys-specific policy is implied.

### Durable async acceptance and recovery

- For asynchronous message-triggered flows, Elwood takes responsibility for the message, stores it and supports recovery plus later retry/replay of the integration, including after failure.
- Durable acceptance must precede acknowledgement to the external broker. Storing a payload alone is insufficient if the system can lose the execution or its scheduling state: the hosting implementation must be able to recover accepted work after a crash.
- Broker acknowledgement does not wait for all downstream business operations to complete; Elwood owns the accepted execution thereafter.
- Retain message payloads and execution history for **30 days by default**, configurable per pipeline. The current three-day state TTL is not the agreed product default.
- Clarified on 6 October 2026: retention also covers **delivered output blobs**, default 30 days, with pipeline defaults and per-destination overrides. Finite, enforced storage cleanup is a day-one requirement on Azure and every other supported provider. Source-owned files are excluded. See [storage retention](storage-retention.md).
- Retention must cover the artifacts needed for recovery/resume/replay, including captured step results and the execution's pipeline/script version. Expiry behavior must be consistent across state and document stores. Retention start time, handling of still-running executions, and deletion mechanics remain implementation details to specify.

### Retry, resume and replay

- Automatic source/output retries follow the policy chosen by the pipeline maintainer.
- Support **resume from the failed step**, reusing successful earlier results and the original execution's pipeline version.
- Also support **start again from the original message** as a fresh replay execution.
- A fresh replay defaults to the original pipeline version. The user can explicitly choose a newer version, for example to apply a corrected transformation.
- The system must distinguish these operations in its API and UI so operators know whether earlier external calls will run again. A replay must retain a link to the original execution for traceability; the exact record model is an implementation decision.

### Version-pinned executions

- Every running execution remains attached to the pipeline version with which it started, including its referenced transformation scripts.
- Editing or publishing a later version must not change the behavior of an in-flight execution.

### Authoring, local testing and activation

The confirmed first-release workflow is **save as draft → validate → test locally → explicitly deploy**.

- Saving edits creates/updates a draft; it does not replace the live pipeline.
- Validation does not publish the draft.
- Developers can test a pipeline locally with synchronous execution, **even when its deployed mode is asynchronous**.
- Explicit deployment activates the chosen version for subsequent triggers. In-flight executions keep their pinned version.
- Git-review/merge approval may be added as an additional deployment policy; it is not a prerequisite of the agreed initial workflow.

Implementation implications: test the chosen draft content, not whichever version happens to be live. The local runner should apply the same dependency ordering, transformations and failure policies with a local execution strategy; it must not rewrite the saved async definition into sync mode or impose a production HTTP-response requirement just to test it. Local tests can return execution results for inspection independently of the deployed trigger's response contract. They do not replace distributed queue/recovery tests.

Local testing supports **both real systems and mocks**. The pipeline maintainer explicitly chooses which to use when starting a local test. Real-system tests execute the configured source/output calls; mock tests substitute connector behavior. Local synchronous execution must not be assumed to mean a dry run. No implicit default, mixed per-connector mode, or mock-fixture format has been specified.

## Implications for the repository assessment

- Evaluate success as a distributable self-hosted product, not merely a successful deployment in one company's Azure environment.
- Define provider-neutral execution, state, document, queue, secret and identity contracts. Existing Azure implementations are one hosting option.
- Installation, upgrades, configuration, backup/recovery and documentation are part of the product deliverable.
- Keep pipeline definitions and transformations portable. Identify provider-specific settings explicitly rather than mixing them into core execution semantics.
- Treat external RabbitMQ triggering and SQL Server stored-procedure execution as identified connector requirements. Keep them separate from provider-specific internal work scheduling.
- Use the order integrations to prove HTTP orchestration and delivery behavior, then the product integrations to prove messaging and longer-running processing.
- Do not make a multi-tenant hosted control plane a prerequisite for the first self-hosted release.

## Proposed approach — not yet agreed

Implement provider-neutral execution semantics with provider-native/serverless hosting packages. A portable container stack is not a substitute for the confirmed deployment requirement. Which provider packages ship first remains open.

First establish correct runtime failure handling, consistent executor semantics and trustworthy execution history. Then prove the identified integrations and package the resulting system for installation by another organization. This replaces any assumption that Azure/Service Bus should define the first release by default.

## Open decisions

Implementation decisions recorded on 6 October 2026: M3 defines whole-run real/mock selection with strict connector fixtures; M4 defines YAML automatic/manual retries, uncertain-write approval, failure-response bindings and terminal retention. The maintainer explicitly confirmed that YAML chooses automatic retry versus an explicit request authorizing another attempt. See [local testing](local-testing.md) and [durable processing](durable-processing.md) for those contracts. Earlier paragraphs retain the original discussion context; those details are no longer open for the reference runtime.

- Required hyperscaler packages and supported internal brokers for the first release.
- Authentication, traffic and destination-side duplicate handling; concrete failure-response bindings and timeout behavior.
- Specialized SOAP/TQL/SAP contracts beyond the M5 HTTP Basic interface. SQL parameter/result contracts are now defined in [connectors](connectors.md).
- Environment promotion requirements and optional Git-review deployment policy.
- Optional mixed per-connector testing is not yet specified.
- Production DSL compatibility constraints and representative regression fixtures.
- Provider-specific retention implementation, sensitive-data controls, upgrade expectations and release acceptance criteria.

## Pet-project hosting inputs for M6

The updated `gommenew/docs/elwood-requests.md` supplies these sizing assumptions: Sweden Central; four suppliers/up to six feeds; at most 30 files/day or 1,000/month; CSV files typically 1–10 MB, up to 25 MB, with JSON output roughly 2–3 times larger; 2–3 concurrent executions; minutes of latency acceptable. Raw input files are retained indefinitely with Blob versioning; Elwood history is 30 days. The existing application costs about $10/month, leaving a target of about €3–5/month for Elwood at steady state. Portal/API usage is low and a roughly ten-second cold start is acceptable. These are planning inputs, not measured prices or a verified budget.
