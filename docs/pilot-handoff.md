# Elwood 0.8.0-rc.1: Deldo and Eagle consumer handoff

Release candidate preparation is in progress. Use a published tag and its passing evidence, not an arbitrary `main` checkout. This document will name the published assets when release verification completes.

**Eagle production gate:** the owner has now supplied volumes far above this profile's retained-metadata capacity, including individual executions above 6,600 outputs. The [Eagle capacity package](eagle-capacity-plan.md) is required before live adoption. The existing distributed profile and measurements below are a bounded functional test profile, not approval to provision it for the full Eagle workload.

Elwood owns orchestration, transformations, delivery history, recovery and retention. The consumer owns its Azure subscription, resource group, networking, business database, third-party connections and deployment approval. This repository does not provision permanent resources for either pilot. SQL stores the consumer's catalog/business data; Elwood state uses Blob/Table, scheduling uses Storage Queue or Service Bus, and Redis supplies shared definitions and reconstructible item status.

## Choose the pilot profile

| Consumer | Runtime profile | Redis | Item execution |
|---|---|---|---|
| Deldo | `files-25m`, 4 GiB workers, one execution per process | `redisConnectionSecretUri` enables the shared definition cache | Whole-file durable flow; no row fan-out required |
| Eagle | `distributed`, 2 GiB workers, explicit source/output fan-out | Required; set `enableDistributedWork=true` | Globally fenced item ownership, ordered aggregation, selected retry and replay |

Both profiles use 64 initialized retention partitions and default to a three-instance Functions ceiling. The distributed worker has an explicit 1.5 GiB managed-heap budget within its 2 GiB instance; the large-file worker retains its verified 3.4 GiB budget within 4 GiB. The instance ceiling applies per scaling group and is **not a spending cap**. External RabbitMQ requires the separately billed always-running ingress host (`enableRabbitMqIngress=true`); it is independent of Elwood's internal queue choice. Both hosts share the same identity, namespace and configuration. Existing ASB can be selected with `queueProvider=serviceBus`; an external RabbitMQ source does not require RabbitMQ internally.

The tested M6 file envelope is documented in [capacity-profile.md](capacity-profile.md). The M7 distributed capacity report must be read separately: emulator timings and request counts are not Azure latency, cloud load certification or a monthly bill. Set a consumer spending budget and measure real traffic before increasing concurrency, quotas or scale. Azure Managed Redis is an additional fixed-cost service; [Microsoft's regional pricing](https://azure.microsoft.com/pricing/details/managed-redis/) and the [Redis template](../infra/azure-redis/main.bicep) expose SKU and availability choices. B0 is a starting pilot size, not a promised capacity for every Eagle workload.

The Redis template's cheaper `highAvailability=false` default is for development/test pilots. Production should enable HA and approve its cost; [Microsoft's availability guidance](https://learn.microsoft.com/en-us/azure/redis/plan-tiers-and-capacity#high-availability-and-replica-implications) requires HA for SLA coverage. Elwood's durable recovery does not make a non-HA cache continuously available.

## Obtain and install

Use the versioned runtime ZIPs and consumer kit from the GitHub release, verify `SHA256SUMS.txt`, and retain `release.json` with deployment records. Alternatively check out the exact tag and run `scripts/package-runtime.ps1`; it refuses a dirty checkout and includes only tracked resources in the kit. Runtime hosts require .NET 10; the installation smoke uses Node.js 22+. NuGet users should reference the matching `Elwood.Pipeline`, `.Azure`, `.Sql`, `.RabbitMq` and `.Redis` candidate versions. The DSL Core stays at 0.7.26; a platform release does not change language semantics.

Use the managed `Elwood.Cli` .NET tool for offline YAML `pipeline` commands. Native CLI downloads support standalone DSL evaluation/scripts/REPL only; use the runtime API or portal for connector execution, durable tests and distributed fan-out. See [the CLI boundary](local-testing.md#offline-cli-boundary).

1. Agree the subscription, dedicated resource group, region and monthly allowance in the consumer session. Inspect quota and current prices. Do not reuse another application's infrastructure without its owner's agreement.
2. Follow [the Azure installer](../infra/azure-small/README.md) for storage, identities, management token hash, portal origins, Functions and optional RabbitMQ ingress. The deployer needs role-definition and role-assignment permissions, not only Contributor. Keep credentials in the consumer's Key Vault; give the runtime identity access.
3. Create or select an RBAC-enabled Key Vault. The optional [Managed Redis template](../infra/azure-redis/main.bicep) creates Redis, writes its TLS connection into that vault and grants the runtime identity Secret User. It emits only the secret URI. It uses authenticated public networking, matching the small runtime template. A private-only deployment requires the consumer's VNet, private endpoints and DNS first.
4. Pass the secret URI as `redisConnectionSecretUri` when applying `infra/azure-small/main.bicep`. Set `capacityProfile` and `enableDistributedWork` as above. Leave the runtime stopped until the fresh retention bootstrap finishes. Redis references remain individual app settings so App Service can resolve them; do not put a Key Vault reference inside the JSON configuration setting.
5. Run `scripts/initialize-azure-retention.ps1` with the same capacity profile before starting workers. It initializes all ledgers once and refuses any existing namespace data. Never reset a missing/corrupt ledger to unblock an existing installation. Restoring control data is an operator recovery operation.
6. Deploy the Functions ZIP using the installer commands. Deploy the ingress ZIP if RabbitMQ is enabled. Allow the first scheduled retention pass to complete; readiness must return 200. An empty action-group list means retention alerts have no notification recipient: connect the consumer's alert destination.
7. Run the installation smoke below. Then configure exact Test and Run secret/connector grants for the pilot, import the bundle as a draft, validate and run the local mock test. Test against controlled real systems next, and deploy explicitly only after reviewing those results.

```powershell
# Set these privately; do not commit or print the bearer token.
$env:ELWOOD_RUNTIME_URL = 'https://<runtime-host>'
$env:ELWOOD_MANAGEMENT_TOKEN = '<operator-token>'
node scripts/verify-installation.mjs                 # Deldo / ordinary profile
node scripts/verify-installation.mjs --distributed   # Eagle / distributed profile
```

The smoke creates a uniquely named synthetic pipeline, accepts 32 items, checks pinned completion, item inspection (distributed mode) and retention health, then retires its route. It calls no third-party systems. Synthetic execution history expires after 15 minutes through normal cleanup. Preserve the returned execution/revision IDs and timestamp; inspect physical cleanup after the next retention ticks. A script error or 503 is a failed install check, not something to bypass with `VerificationOnly` or an empty ledger.

The portal remains a separately deployed Next.js application. Use portal commit `95c39885c584817d8ae9233fca9a0c9202a5353c` ([passing CI](https://github.com/max-favilli/elwood-portal/actions/runs/42224723364)) or a later independently verified release. Configure its runtime origin, HTTPS and matching CORS origin according to its README. Existing controls include draft real/mock testing, item/attempt/receipt inspection, selected recovery and separately acknowledged replay. Check session scope with an ordinary operator account, not just the initial wildcard bootstrap account.

## Deldo integration

Start from [sample-deldo-stock](examples/connectors/sample-deldo-stock/pipeline.elwood.yaml), the consumer's verified `pipelines/deldo-stock` bundle and its `normalized-offer.schema.json`. The consumer's real column map and SQL schema remain authoritative. The sample is an example, not a deployment containing production URLs.

The intended flow is Event Grid BlobCreated → normalize the event → pull the announced Blob with `ifMatch` ETag and `maxBytes: 26214400` → parse/normalize CSV → call `catalog.usp_ImportOffers` with the whole output JSON as `@payload` → deliver a JSON snapshot. The SQL procedure's first result set is recorded for inspection. Event Grid batches are split into individual durable acceptances, and subscription validation is supported. If the supplier uses SFTP, the consumer must arrange landing in Blob storage; Elwood does not implement an SFTP listener.

Update the consumer's earlier target YAML before deploying it:

- Add `eventGrid: { subjectPrefix: /blobServices/default/containers/inbound/blobs/deldo/ }` to the HTTP source; optionally constrain `topic` to the exact storage account resource ID. This is an object, not a boolean.
- Add trigger `auth: { type: basic, user: $secrets.eventGrid.user, password: $secrets.eventGrid.password }`. Configure Event Grid's secret `Authorization` delivery header to match, including subscription validation; grant the trigger access to those exact secrets.
- Set the Blob pull's `maxBytes: 26214400`; its default is 10 MiB. Keep the announced `ifMatch` ETag.
- Use the owned snapshot container/prefix described below and `overwrite: true` for the deterministic snapshot name. Keep uncertain-write retries conditional on this being safe for the consumer.

The existing `normalize.elwood` numeric guards and event validation remain necessary. The consumer's requests for callable let-bound lambdas, strict `toNumber`, `fail(message)` and consistent strict member access are separate DSL follow-ups, not new language behavior in this platform release. Keep the verified script workarounds. JSON snapshot delivery is implemented; pipeline Parquet delivery is not.

Change the sample snapshot destination from its illustrative `analytics` container to the installation's **owned output container**, normally `outputs`, under `elwood/deldo/...`. Use that account's identity or a separately scoped connection secret. Configure the pipeline's Blob Run/Test prefix grants. The installed Azure profile refuses delivery into an account/container outside its verified retention boundary. Supplier input files belong in an inbound container/account outside owned output retention; only read access is needed there.

The exact configuration keys and a scoped JSON example are in [connector identity grants](connectors.md#scoped-access-to-host-identities): `Elwood:ConnectionGrants:<pipeline-id>:Run` and `Test` contain `BlobPrefixes` and, for SQL identity authentication, `SqlIdentityServers`. Named credentials separately require `Elwood:SecretGrants:<pipeline-id>:Run`/`Test`. Mirror these keys through the installer's private `additionalSettings`; do not rely on an account-wide grant to access the runtime's output prefix.

Keep `mode: async` for 25 MiB files: the HTTP/Event Grid response confirms durable acceptance, and the worker processes the file afterward. A small file can use a synchronous flow, but gateways and the configured sync budget impose a deadline; a large file is not made safe by putting a synchronous call inside a Function. All executions keep their starting pipeline revision.

The consumer's idempotent SQL import key `(blobPath, ETag)` prevents exact event redelivery from duplicating offers. Optional Elwood SHA-256 source deduplication also skips identical bytes under different names/ETags when configured; default behavior processes them. Dedup history defaults to 30 days and explicit replay can override it. Agree whether unchanged content with a new ETag should represent a new import before enabling skip.

Keep manual handling for uncertain SQL writes until the consumer confirms its import procedure is idempotent. A timeout may have committed. Inspect the import key before resuming. Snapshot writes may permit automatic uncertain retries only when the same destination name and content make them safe. Retention defaults to 30 days for internal history and delivered snapshots, configurable per pipeline/destination; input blobs are never deleted by Elwood.

Consumer acceptance: real CSV headers/decimal/season mappings; rejected rows; small and 25 MiB files; exact event redelivery; same-content/new-ETag policy; replaced-Blob ETag rejection; SQL timeout after commit; snapshot permissions; expired history/snapshot cleanup; one representative day of telemetry and costs.

## Eagle integration

Start from [sample-product-sql](examples/connectors/sample-product-sql/pipeline.elwood.yaml) and [distributed-fanout](examples/distributed-fanout/README.md). Configure the external RabbitMQ connection/queue and acknowledgement behavior, SQL procedure/parameters/result shape, and SAP PI/PO HTTP endpoint with Basic authentication. Those values and the data mappings must come from Eagle; do not infer production schemas from the fixture. SOAP/XML can be sent through the HTTP connector when the required headers/body are supplied, but this pilot's agreed interface is HTTP with Basic auth. No dedicated TQL connector is promised by this release.

Use source `path` when an array should create one pull per selected item, and explicit output `fanOut: { path, concurrency, maxItems }` for item deliveries. Output `path` alone still selects one value processed once. `$slice` and `$index` identify the selected item in expanded work; keep `$idm` for the immutable stage context. Local real/mock tests use the same selection and mappings, sequentially, and mocks can be supplied by item ordinal.

Start with `concurrency: 4` and a bounded `maxItems` matching the tested batch. Increase only after measuring downstream SAP/SQL limits and host memory. The schema's 100,000-item/1,000-concurrency ceilings are protective limits, **not** a certified operating envelope. Aggregate selected input and output have a 16 MiB bound; split larger application batches upstream. Avoid embedding the entire catalog or message backlog in each item's stage context.

Redis is an acceleration/status layer. Durable Blob manifests, permits, checkpoints and receipts decide whether a call may run. Redis loss does not grant a new permit or forget a completed delivery. Item versions/expiry prevent stale updates, and recovery rebuilds cache projections. Missing Redis makes readiness unhealthy; recovery and cleanup keep retrying independently. This correctness contract does not promise uninterrupted service during provider outages.

In the portal inspect failed/review items and their attempts before selecting retries. Retry stays on the original execution/revision and preserves completed sibling deliveries and destination receipts. An uncertain write requires the authored policy or explicit acknowledgement. Replay creates a new whole execution; choose original or a selected published revision and separately acknowledge possible duplicates and uncertain writes. Do not use replay as a substitute for selected retry.

Consumer acceptance: genuine RabbitMQ disconnection/redelivery; durable acceptance before acknowledgement; SQL row/message join; SAP Basic auth and response mapping; bounded concurrency at the agreed peak; process restart during a call; timeout after SAP accepted a write; selected retry without repeating siblings; cache failover/loss; item inspection with restricted permissions; retention and capacity backpressure; measured transactions, Redis memory, telemetry and monthly cost.

## Operating bounds and open consumer decisions

The partitioned profile defaults to 2,048 reserved objects and 16 GiB per partition, 1,000 registrations and 1 GiB uploaded per hour per partition, 100,000 work items and 1,000,000 reserved attempts per parent partition. Every journal version and retained document consumes accounting; counts are not merely current Blob counts. A stage reserves 100 potential attempts per item. The installation cannot borrow capacity across partitions. `/api/retention` reports the tightest remaining partition allowance, so total free storage alone does not prove another batch fits.

These finite limits intentionally stop unlimited growth. Do not raise `MaxObjects` above the supported 2,048/partition to bypass the 4 MiB control-ledger bound. For sustained workloads that exceed the measured retention runway, shorten an explicitly approved pipeline history policy, reduce batch/frequency, or add a separately scoped installation; larger-scale metadata storage is a separate capacity design. Never delete live ledgers, journals or Redis keys to pretend deliveries completed.

For scale context, the first completed 2,048-item delivery measurement registered **42,707 accounting objects**, about one third of the installation's nominal 131,072 slots. Partition distribution, other executions and hourly admission limits reduce usable headroom further. This supports a bounded functional trial; the production volumes supplied on 6 November require the [Eagle capacity package](eagle-capacity-plan.md). The 30-day default is unchanged. See the [measured run and its cleanup failure](evidence/m7-pilot/processing-f7bec87.json) and [current verification status](m7-pilot-evidence.md).

When short history expires before the original execution deadline, physical item cleanup keeps a closed admission reservation until that deadline plus one day. It remains counted against capacity until the independent sweep removes it; this prevents stale workers from reopening deleted work. A missing parent therefore does not imply that every retention obligation is already released.

Scheduled distributed cleanup drains multiple restartable pages within a two-minute work budget per pass; a busy owner or provider outage yields to a later tick. Confirmed document/journal accounting is released only after physical cleanup. Checkpoint accounting is released in batches only after the parent and its versions/snapshots are absent. Unconfirmed uploads retain their finite, charged one-day reconciliation grace; a late confirmation cannot extend an existing cleanup request.

Native lifecycle/RBAC, Managed Redis SKU availability, private networking, real cloud load/failover and both business-system acceptance runs belong to the consumer deployments. Passing component/emulator tests does not replace these installation checks. Send failures back with release/commit, execution/revision and stage/item IDs, a redacted error, timestamps and the smoke result; never send tokens, connection strings or real customer payloads.

## Prompts for the two consumer sessions

**Deldo session:** “Adopt the published Elwood 0.8.0-rc.1 pilot candidate using `docs/pilot-handoff.md` and the versioned consumer kit. You own provisioning and costs in this project's approved resource group. Keep the verified normalized-offer contract and idempotent SQL import procedure. Configure `files-25m` with the optional Redis definition cache, Event Grid, scoped secrets and owned snapshot retention. Run the installation smoke, local mocks, controlled real tests and Deldo acceptance list before connecting supplier production delivery. Report any runtime gap to the Elwood session; do not invent unsupported YAML.”

**Eagle session:** “Adopt the published Elwood 0.8.0-rc.1 pilot candidate using `docs/pilot-handoff.md` and the versioned consumer kit. You own provisioning and costs in this project's approved environment. Configure the distributed Redis profile and external RabbitMQ ingress, then implement the agreed RabbitMQ → SQL procedure → SAP PI/PO HTTP Basic-auth integration. Use explicit bounded fan-out and per-step retry policy, and exercise item inspection/selected recovery. Run the installed distributed smoke and Eagle acceptance list at measured load before production activation. Preserve durable receipts and report gaps with redacted execution/stage/item identities.”
