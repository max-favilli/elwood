# Eagle capacity package

Status: **required before declaring Eagle ready for live use**. Added 6 November 2026 after the owner supplied production-volume observations. No consumer infrastructure is provisioned by this package. Deldo's separately verified `files-25m` profile remains unchanged.

## Workload and units

An incoming event normally creates an execution. An explicitly expanded source pull or output creates a work item within that execution. A database result containing many rows is not automatically many work items: YAML fan-out determines that boundary. Do not hide required item inspection or recovery by arbitrarily grouping unrelated events.

Use the owner's rounded figures as design inputs, not as a measured Elwood capacity claim:

- About 20,000 incoming events on a median day; about 47,000 at the daily p95.
- A burst day near 400,000 incoming events.
- About 625,000 events and 1.1 million output records in a recent 30-day window.
- About 61,000 source/payload rows on a median day, including trigger and child payloads; a maximum above 600,000.
- Individual executions exceeding 6,600 outputs or 2,100 pulled inputs. The next full batch check must cover at least 7,000 expanded items.
- Thirty-day history by default, configurable per pipeline/destination. Bursts should produce bounded backlog/backpressure instead of turning healthy business operations into failed executions.

The exact Elwood item count requires the first pilot's YAML and cannot be obtained by summing all these overlapping production counters. Body sizes, arrival rates within the burst day and acceptable queue delay are separate sizing inputs.

## Why the existing profile is insufficient

The fixed 64-partition Blob ledger holds at most 131,072 accounting objects. A completed 2,048-item fixture registered 42,707 objects. This covers a bounded functional pilot, not the production retention envelope above. Increasing the JSON ledger beyond its 4 MiB bound, reducing history silently or relabeling events as larger work units would not solve the requirement.

The 2,048-item delivery/recovery checks completed, but both full tests exceeded 60 minutes during cleanup, including the bounded-parallel deletion correction at `52d1904`. That run processed the items in 2,998 seconds with 943,148 SDK requests and 42,762 accounting objects. Those results remain useful regression evidence; they do not establish Eagle's production throughput or the required retained cardinality.

## Implementation direction

Use Azure Table storage for scalable accounting metadata and expiry/ownership indexes. Keep payloads, durable manifests, checkpoints and receipts in Blob storage; keep Redis as a reconstructible cache. This changes the metadata mechanism, not the DSL or the durable ownership contract. It adds no new Azure service family to the existing installation.

The Table provider must use point operations and bounded pages. Whole-ledger download, deserialization, hashing or listing on admission, inspection or maintenance is prohibited. Quota counters and record/index intents must commit atomically within their accounting partition. Closing a parent and admitting another stage must retain the current generation fence. A lost response must be recoverable without double charging, forgetting a possible write or releasing a newer owner's capacity.

Maintain finite configured limits for retained bytes/objects, active work and admission rate. Size these from the workload and payload measurements; a larger ceiling is not a spending cap. Preserve reserved capacity for terminal checkpoints and cleanup. Use indexed, restartable expiry and execution cleanup, including late uploads, versions and business-output obligations. Health checks aggregate bounded counters and due-work observations instead of enumerating every retained record.

Capacity pressure before a business attempt should defer scheduling without consuming its business retry allowance. At the ingress boundary, return explicit retryable backpressure or leave broker work unacknowledged when durable admission cannot be guaranteed. Already accepted work retains its revision and durable recovery state. Finite authored execution/backlog deadlines remain explicit; this is not a promise of unlimited queuing through an indefinite outage.

Select the new provider through an explicit installation layout/profile. Keep the existing bounded Blob-ledger implementation for existing installations and Deldo. Fresh bootstrap must remain explicit and fail closed when control data is missing. Changing a populated installation's layout requires a planned offline migration, never an automatic reset.

## Work breakdown

- [x] Record the production sizing input and distinguish executions from expanded work items.
- [x] Audit retention/accounting call sites and define the bounded read/health contract, preserving existing small-ledger APIs. See the call-site audit below; paged mutation integration remains open.
- [ ] Implement Table point operations, atomic quota/index transactions and lost-response recovery.
- [ ] Implement bounded due-record and execution indexes, expiry, generation-conditional release and counter-based health.
- [ ] Preserve late-write ownership, item/attempt reservations, terminal-checkpoint headroom and independent output retention.
- [ ] Add scheduling/ingress backpressure with no duplicate deliveries or spurious terminal failures.
- [ ] Wire the provider into installed API/Functions/ingress registration, explicit bootstrap and Bicep settings.
- [ ] Prove operation and memory bounds at production-scale metadata cardinality; exercise concurrency, throttles, lost replies, missing control rows and cleanup interruption against SDK transports and Azurite.
- [ ] Complete an installed batch of at least 7,000 items, recovery, selected retries and physical retention. Measure normal and duplicate/burst delivery separately.
- [ ] Publish measured limits, deployment choices, cost-model inputs and updated versioned consumer handoffs.
- [ ] Consumer-owned native load/failover and monthly-cost acceptance using the actual pilot mappings and payload sizes.

No release is Eagle-production-ready while the required implementation and verification boxes above remain open. Native service pricing, provisioning approval and final business acceptance belong to the consumer sessions.

## Call-site audit and first implementation package

| Caller | Required accounting operation | Current change / remaining integration |
|---|---|---|
| `BlobOutputRetention.SweepAsync` | Bounded due-artifact page, parent point lookup, generation-conditional removal | Uses `ReadMaintenancePageAsync` with a 256-record maximum; failed pages move behind older untouched obligations. No installation snapshot |
| `BlobOutputRetention.HealthAsync` | Counter/health summary with bounded failure examples | Uses `ReadHealthAsync`; no snapshot or separate capacity scan at this boundary. Legacy Blob providers compute their bounded partition summaries |
| `AzureExecutionStateStore.CleanupArtifactsAsync` | Bounded execution-reservation reconciliation queue | Uses the bounded maintenance API; no installation snapshot |
| State acceptance / checkpoint writes | Admission preflight, owned registration, generation renewal, reserved terminal/tombstone slots | Table provider must implement point transactions; physical state leases remain authoritative |
| Document/journal uploads | Register before upload, confirm exact ownership, retain late-write obligations | Table provider must preserve these operations and their generation fences; payloads stay in Blob storage |
| Stage admission and attempt dispatch | Parent-and-stage fence, finite item/attempt budgets, lost-response reconciliation | Table primitive supports an atomic open-parent fence; high-level idempotency and scheduler backpressure integration remain open |
| Parent cleanup | Close admission, expire internal obligations, release confirmed-prefix accounting, keep stale-admission fences | Requires restartable execution-index pages; existing small-ledger methods still scan their bounded partition |
| Installer / health | Explicit layout initialization, persisted configuration, missing-control rejection | Existing layouts are unchanged; Table profile/bootstrap not yet exposed |

`TableRetentionAccounting` is an internal storage primitive, **not an activated hosting profile**. It commits one record, quota counters and maintenance/expiry/execution/error indexes in a single partition transaction. Mutations use point reads; service queries return one page of at most 256 rows. Every mutation has an operation identity. A lost reply is acknowledged only when the stored identity proves that mutation committed; an intervening replacement returns an unknown outcome instead of blindly applying the mutation again. The caller must reconcile unknown outcomes by the higher-level reservation/attempt identity. Do not translate them directly into a business failure or retry an increment automatically.

The installer-only empty-partition initializer refuses existing orphan rows, and normal admission refuses missing or incompatible controls. The eventual outer layout marker must additionally prevent reinitializing a previously ready empty partition whose quota row was deleted. This primitive does not replace that installation fence.

The initial local regressions exercise transaction conflicts, lost replies, generation replacement, missing controls, bounded queries and the parent-close fence. The real SDK/Azurite transaction regression and installed HTTPS smoke pass at `7061e7b` in [CI 42244834223](https://github.com/max-favilli/elwood/actions/runs/42244834223); the full capacity run is separate. The first service test correctly rejected a fixture retry that accidentally generated a new ownership generation; the corrected fixture reuses the exact original request. Whole-provider expiry, retained-cardinality checks and the installed 7,000-item gate remain open. Also include reserved checkpoint bytes in sizing: the existing parent reservation is three 4 MiB slots per execution, a conservative logical reservation rather than a measurement of bytes actually stored or billed.
