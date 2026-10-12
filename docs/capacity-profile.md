# Deldo capacity profile — measured Azure verification

8 October 2026. The owner prioritised the full **25 MiB / 1,000 files per month** workload over the earlier €3–5/month hosting target, with hosting cost to return for approval. Temporary hosted verification uses `elwood-m6-rg` in West Europe within the existing €10 campaign allowance; permanent adoption is not approved.

## Measured workload

The offline probe runs the actual `D:/git-repos/gommenew/pipelines/deldo-stock/normalize.elwood` script against a generated CSV made from the first valid sample row. It exercises the durable source/transformation/output runner, filesystem documents and two counting destinations. It makes no external writes. This tests the current placeholder schema and transformation, not an authentic full supplier feed or cloud/database performance.

| Probe | Result | Elapsed | Peak process working set | Internal document writes | Largest JSON document |
|---|---|---:|---:|---:|---:|
| 1 MiB CSV / 13,979 rows | Completed | 2.48 s | 248 MiB | 25.66 MiB | 3.52 MiB |
| 25 MiB CSV / 349,524 rows, 1 GiB managed heap | Failed before delivery: out of memory | 2.39 s | 496 MiB | partial | — |
| Same, 2 GiB managed heap | Failed before delivery: out of memory | 23.62 s | 2.03 GiB | partial | — |
| Same, 3 GiB managed heap | Completed | 29.02 s | 2.87 GiB | 641.34 MiB | 88.00 MiB |

Each successful large run delivered 88 MiB twice (SQL-shaped payload and snapshot-shaped payload). Only the snapshot would occupy Blob business-output storage. The retained internal writes include raw fetched data, mapped data, IDM, output payloads and compatibility aliases. No DSL behavior was changed. Duplicated synthetic rows make this a sizing fixture; scripts, row shapes and rejected-row counts affect memory/expansion materially. A byte limit alone cannot bound every possible authored transformation.

Reproduce from the backend repository (the tool removes only its own generated temporary directory):

```powershell
$env:DOTNET_GCHeapHardLimit = '0xC0000000' # 3 GiB managed heap
dotnet run --project tools/Elwood.CapacityProbe -c Release -- `
  D:/git-repos/gommenew/pipelines/deldo-stock/normalize.elwood `
  D:/git-repos/gommenew/pipelines/deldo-stock/samples/deldo_stock.csv 25 1
```

## Implemented profile

Use `capacityProfile=files-25m` in `infra/azure-small/main.bicep`. The default remains `guarded`. Hosted verification covers the generated Deldo fixture below; authentic supplier files and database throughput still require project acceptance.

- 4,096 MiB Flex worker, explicit **3.4 GiB** managed-heap ceiling (`DOTNET_GCHeapHardLimit=0xD9999999`) and `DOTNET_GCConserveMemory=9` to compact fragmented large-object heaps. The explicit byte limit takes precedence over the 85% setting and does not depend on host-reported physical memory. The original 75% candidate passed two of three hosted large runs but exhausted managed memory on the third; standalone results alone were insufficient. One active transformation/recovery/synchronous request per host; horizontal instances provide concurrency. HTTP admission is held through response serialization. Queue batching and Service Bus concurrency are set to one. No always-ready instances.
- 128 MiB maximum execution document. A Blob source must explicitly set `maxBytes: 26214400` for a 25 MiB input; its normal default remains 10 MiB. Use async live pipelines for this workload.
- 64 independent retention partitions, each with the existing ETag CAS semantics and 4 MiB metadata ceiling. Each admits at most 2,048 object slots, 16 GiB reserved bytes, 1,000 registrations/hour and 1 GiB artifact writes/hour. Aggregate ceilings are 131,072 slots, 1 TiB reserved bytes and 64 GiB/hour. Individual partitions can fill earlier; capacity cannot be borrowed silently.
- These are protective ceilings, not prepaid storage or spending caps. Failed cleanup/corrupt ownership remains visible; admission for an overdue/full partition closes. Twelve MiB and three emergency slots per accepted execution still protect final/error/deletion checkpoints.
- The measured 1,000-file accounting test models 49,000 cleanup obligations with payload sizes rounded upward. All partitions stay below their size/slot/byte limits; generation fencing, emergency checkpoints, execution-specific expiry and unrelated output retention remain intact. It does not upload a terabyte.
- Empty ledger mutations no longer write new control-Blob versions. Reservation maintenance uses a conditional per-entry update instead of repeatedly loading all ledgers. Sweep timestamps live in a small separate record.
- History inspection is bounded to 64 KiB per document, 256 KiB aggregate and 64 document previews. Large documents remain retained; the portal explicitly labels omitted previews. Draft-test response bodies similarly omit oversized outputs and report `omittedOutputs` byte counts. Small results retain their existing shape.

The retention layout marker is immutable. `guarded` and `files-25m` cannot write the same installation concurrently. A populated single ledger cannot switch automatically to the partitioned profile; a missing marker plus artifacts from the other layout fails closed. A fresh installation is the supported deployment path. Existing installations need a separately verified offline migration; never delete a marker/ledger to change profiles.

## Hosted evidence

The final fixed-heap run, 8 October 2026, completed three simultaneous integrations in **54.151, 53.348 and 48.883 seconds**. All three file transformations overlapped (18:01:28–18:01:59 UTC), demonstrating separate workers under the single-work-per-host gate. Azure's sampled maximum working sets for those workers were 3.22, 3.17 and 2.92 GiB. Samples are not a proof of the instantaneous peak for every possible script.

Each run read 26,214,389 bytes / 349,524 CSV rows and delivered two 92,274,677-byte JSON payloads. The second independently uploaded identical file ended `Skipped`, with no output objects. An explicit forced replay completed in 43.587 seconds and delivered again. Inspection was 19,840 bytes, with oversized stored documents explicitly omitted. Final executions: `9076426a-c25a-431a-90a8-09eb2f4f654b`, `a800f6b6-3e1a-4df6-aaec-b346d896dd19`, `1669745f-3b45-4e90-b31f-0f042e4277cb`; skipped: `6577395b-a553-4b39-b85f-b0366fd96424`; replay: `3acbac32-6565-4b85-bf13-73aa467d056d`.

The monthly metadata model used **49,000 obligations / 51,000 reserved object slots**, 1,000 small synthetic state objects, 780,845,056,000 reserved bytes, and 28,459,608 bytes of ledger JSON. Its largest partition was 475,063 bytes. It lived in separate `capacity-control` and `capacity-state` test containers, not the execution containers. A scheduled native retention pass used **60,211,200 MB-ms = 58.8 GB-s**, or **14.7 seconds at 4 GiB**. The pass scanned the full ledger and checked a bounded 100 execution reservations; no payload deletion was due in this model. A manually invoked pass also completed and left health green. This is metadata-scale evidence, not a terabyte upload or a full month of elapsed operation.

After restoring the real test namespace, ordinary bounded maintenance removed all expired history and registrations. At 18:17 UTC, history count, registered objects and registered bytes were zero with healthy retention. Blob inventory found zero live delivered outputs/versions/snapshots; six source blobs (157,286,334 bytes) were still present. Bounded seven-day soft delete is accounted for separately below.

Verification: Pipeline 227, Azure non-integration 25, Hosting 12, five isolated API contracts; portal 120 Node checks (one opt-in check skipped), ten component tests, build and lint (zero errors, three existing warnings). .NET 8 compatibility and Functions/Bicep builds pass. Independent review findings about fingerprint cleanup, memory admission, layout safeguards and omitted UI data were resolved. No DSL behavior changed.

## Cost basis for approval

The following is a **monthly projection from hosted workload/maintenance measurements and retail meters**, not an observed monthly invoice. It assumes all 1,000 files are 25 MiB, 30-day retention and the measured transformation. Supplier data and database throughput remain acceptance inputs.

Retail API checked 8 October 2026, West Europe, using **`api-version=2023-01-01-preview`**: Hot LRS storage €0.017247/GB-month; write/list operations €0.047518/10,000; reads/other operations €0.003784/10,000; Flex on-demand time **€0.000023/GB-second** and executions €0.000004 per ten calls above grants. The older API response rounded the small EUR compute rate to zero; the newer API resolves it. Shared subscription grants are conservatively excluded. Sources: [Azure retail price API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices), [Functions pricing](https://azure.microsoft.com/en-us/pricing/details/functions/), [metric conversion](https://learn.microsoft.com/en-us/azure/azure-functions/functions-consumption-costs).

| Component | Basis | Monthly estimate |
|---|---|---:|
| Internal payloads + one business snapshot | 764.77 GB at 30 days; 37/30 allowance for soft delete | €16.27 |
| Integration compute | Final workers recorded 725.2 GB-s for four completed runs; about 181.3 GB-s/file | €4.17 |
| Retention compute | 58.8 GB-s/pass × 8,640 five-minute passes | €11.68 |
| Recovery baseline | Observed 4 GB-s per minute × 43,200 | €3.97 |
| Retention Blob transactions | About 332 reads and 101 writes/pass; model of adapter calls | €5.23 |
| Control versions, admissions and additional operations | Rewritten ledger partitions; conservative version + soft-delete allowance | €3–5 |

The table sums to approximately **€44–46/month**. The **€55 planning ceiling** adds roughly €9 (20%) contingency to its upper end for extra deletion work, cold starts and destination latency; it is not a measured charge or hard cap. The proposed **€60 first-month allowance** remains subject to consumer approval. The newer installer’s Application Insights/Log Analytics and retention alert, plus optional Redis/SQL/application hosting, are additional and must be priced by the consumer before provisioning. A month dominated by smaller files uses less payload storage and transformation compute, though scheduled maintenance still has a baseline cost.

Excluded: source-owned supplier storage, the existing app, SQL/database capacity, portal hosting, external RabbitMQ ingress, cross-region egress, tax and paid logging. The two-Blob capacity fixture measures payload delivery; it does not establish SQL procedure latency or SAP behavior for the final projects. Account discounts or unused free grants may reduce the bill.

## Remaining live verification and approval

- [x] Reproduce the real Deldo normalization script at 25 MiB; verify required heap and payload expansion.
- [x] Implement partitioned, bounded storage admission and model a month of 1,000 maximum-size files.
- [x] Build candidate worker/document limits, memory admission and bounded history inspection.
- [x] Verify the final build against live Azure, including ~88 MiB document writes/reads under the 128 MiB limit and actual cleanup; layout safeguard regressions pass separately.
- [x] Run three independent 25 MiB integrations concurrently on separate workers; prove no duplicate destinations for independently uploaded equal files.
- [x] Measure hosted execution time, full-ledger retention and recovery baseline; price transactions/control versions from the measured model and verify the EUR retail compute rate.
- [ ] Approve the measured hosting estimate before permanent adoption.
- [ ] Run the actual supplier file/schema and SQL import through the Deldo project; this synthetic test does not replace real acceptance.

The live campaign used temporary `elwood-m6-rg` in West Europe, consumption compute plus dedicated LRS storage only, within a **€3 incremental working allowance under the already approved €10 total**. The exact group and tags were checked before deletion; **`az group exists` returned `false`** afterward. No SQL server, always-running RabbitMQ host, ACR or paid logging workspace was provisioned.

The final metric snapshot recorded 3,416,883,200 MB-ms (3,336.8 GB-s), 410 invocations and zero always-ready usage: approximately €0.077 of compute at the sampled paid rate, before grants and excluding other meters. Cost Management still reported only €0.000529 of storage charges and was plainly delayed; that is not the campaign invoice. Provisioned duration and measured usage keep this lab comfortably within the remaining allowance.

Reproduction harnesses: `scripts/capacity-hosted.test.mjs` (explicit `ELWOOD_CAPACITY_LIVE=true`, script/sample paths, private management/trigger credentials and the documented disposable host) and `scripts/capacity-ledger-probe.py` (`seed`, `select`, `measure`, `restore`, then `clean` after history expiry). The latter requires Requests, supplied by Azure CLI's Python, separate `capacity-control`/`capacity-state` containers and temporary data access limited to those test containers. It switches only this guarded lab's namespace/configuration, restores its original policy/settings, and must never run on a shared installation. Secrets and raw evidence stay in ignored `.private`; the relevant evidence and limits are recorded above.
