# M7 pilot release evidence

Updated 6 November 2026. **Release verification is in progress.** This document distinguishes component checks, installed-profile emulator checks, published artifacts and consumer-native acceptance. A failed run is not a certified capacity envelope.

## Reproduction and environment

Run the repository CI workflow or, with Docker available:

```sh
dotnet test dotnet/tests/Elwood.Runtime.Hosting.Tests --filter Category=Integration
```

`InstalledDistributedTests` creates real Redis 7.4 and Azurite 3.35.0 containers. Two independent service providers use the production API/Functions registration, finite quotas, the retention health guard and accounted storage journals. Only credentials/endpoints and the native ARM policy probe are substituted. Eight queue consumers exercise a four-permit stage; every queue notification is dispatched twice concurrently. The test clears Redis during processing, verifies one HTTP delivery per item, checks ordered completion and accounting, then physically removes expired item history and projections. A closed admission reservation intentionally survives early parent expiry until the original deadline plus one day.

Azurite does not implement Azure RBAC, native lifecycle/versioning, network latency or billing. Two service providers are independent runtime instances in one test process, not an Azure scaling/failover experiment. Separate regressions cover interrupted calls, lost checkpoint replies, stale owners, selected retry receipts, replay, cache rollback, late uploads and cleanup after interruption. None of these results certifies a regional Managed Redis failover or the pilots' real business systems.

## Capacity corrections and retained failures

| Run | Observation | Result |
|---|---|---|
| [42199957814](https://github.com/max-favilli/elwood/actions/runs/42199957814), `d5dd309` | Initial 2,048-item run exceeded 25 minutes waiting for parent ownership | Failed; no capacity claim |
| [42203742891](https://github.com/max-favilli/elwood/actions/runs/42203742891), `e58c4b1` | Last progress snapshot: 1,247/2,048 HTTP calls, 1,482.924 seconds, 816,362 SDK requests | Failed at 25-minute budget |
| Same run, eight-item case | Execution completed in 5.186 seconds; 4,029 SDK requests; 207 registered objects; 14,947,489 registered bytes; 196,059,136 peak process bytes | Delivery succeeded, test failed because it incorrectly expected immediate release of the closed admission reservation |
| [42207158028](https://github.com/max-favilli/elwood/actions/runs/42207158028), `b202ad0` | With read-only parent guards: last progress 1,292/2,048 deliveries, 1,485.228 seconds, 634,472 SDK requests | Failed at 25 minutes during an item ownership validation; the eight-item processing and cleanup case passed |

The scheduler now ignores duplicate/busy hints that made no progress, rejects already settled hints before taking a parent claim, and uses a bounded next-dispatch cursor. Real completions observe one item and publish immediate failed-item diagnostics; periodic recovery keeps its independent bounded scan. Dedicated aggregation continuation hints publish ordered results without redispatching connectors. A 260-item out-of-order regression completes without periodic recovery.

Parent validation now uses the same authoritative Blob lease through a read-only guard. It does not rotate admission reservations or create Table index intents for a read. The SDK regression verifies exclusion against state writers, loss of ownership, missing IDs and zero state/ledger/Table writes. Mutating parent transitions retain their original indexed, accounted claims.

The Linux concurrency regression now allows a brief duplicate's parent validation while both connector calls remain blocked; it still fails if connector execution holds the parent claim. The retention test checks the actual closed-reservation contract rather than deleting its stale-admission fence to satisfy an incorrect expectation.

The next correction uses ETag-conditional retention-ledger reads, retaining a bounded private snapshot per partition. Every lookup still contacts authoritative Blob storage; deletion, corruption and transport failure do not return cached state. Callers and failed mutations receive independent entry copies. Mutations retain cross-worker ETag compare-and-swap and serialize only within one ledger instance to reduce self-contention. SDK regressions verify remote changes, caller isolation, failed mutations, corruption/deletion and a forced competing write. This correction still requires the installed load run before any capacity claim.

## Packaging checks completed so far

The clean `3f9958d8` checkout produced framework-dependent .NET 10 Functions, API and ingress ZIPs and a tracked-file consumer kit. All four include the MIT license; archive inspection found no `.private`, `local.settings.json` or `secrets.json` entries. The Functions archive includes its hidden `.azurefunctions` directory and native binding metadata. Core's assembly version remains **0.7.26.0**. `SHA256SUMS.txt` covers all four ZIPs, and `release.json` records the source commit. These local archives predate subsequent scheduler corrections and are **not the final release**.

All five HTTP API contracts passed against that published API DLL, including process restart, recovery permissions, sync cancellation, scoped connectors and Event Grid acceptance. Portal code passed 120 Node checks and 24 component checks; its separate live Azure browser test was skipped.

The clean `e4d8f0347157e18fba33e959efd99dc9b40f9c9e` checkout repeats these checks with the read-only parent guard and scheduler corrections: all five API contracts pass against the packaged DLL. The durable contract also executes the shipped `verify-installation.mjs` client through real save/deploy/accept/complete/retire endpoints and confirms retained execution history. Its explicit test-only HTTP option accepts literal loopback addresses; remote installations still require HTTPS. This local client check does not replace the distributed Azure installation smoke. Archive inspection again confirms license, absence of private configuration, Core 0.7.26, provenance and all 37 hidden Functions support entries. Portal commit `05778bae8b38b32a4fea262ec4cf7be3eaab4e07` has [green CI](https://github.com/max-favilli/elwood-portal/actions/runs/42206850692).

Current candidate source and release availability must be taken from the final publication entry, not inferred from these package checks.

## Consumer acceptance remains separate

Use [the pilot handoff](pilot-handoff.md) for native installation/bootstrap, the installation smoke, real mapping/credential decisions and Deldo/Eagle acceptance lists. The consumer sessions own resource approval, native deployment, representative load/failover and measured monthly spending. No permanent consumer infrastructure was provisioned for this verification.
