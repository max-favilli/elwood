# Small Azure profile — M6 work in progress

The first deployable unit is **synthetic test storage only**, not a production Elwood installation. `test-storage.bicep` provisions one new Standard LRS StorageV2 account with private Blob containers, a queue and a table. It enables blob versions, seven-day soft delete and a seven-day lifecycle backstop on the three contract-test containers. The supplied operator gets Blob Data Owner and Queue/Table Data Contributor roles on this account only. Shared keys and anonymous Blob access are disabled. Authentication uses the operator's existing Azure CLI session; no credentials appear in template outputs.

Blob version deletion requires `deleteBlobVersion/action`, which Blob Data Contributor lacks. The live probes reproduced a 403 `OperationNotAllowedOnAutomaticSnapshot` with that role. Blob Data Owner is the built-in role supporting this operation; it grants data access on the disposable account, not subscription ownership. A production cleanup identity can instead use a custom role containing the required version-delete action, scoped to its owned containers. See [Azure's version deletion permissions](https://learn.microsoft.com/en-us/azure/storage/blobs/versioning-overview#azure-rbac-action-to-delete-a-blob-version).

There are no existing-resource references, Redis, SQL, AI, paid compute plans, public application endpoints or deployment service principals. Do not use the sibling `infra/azure` Terraform template for this test: that older template expects shared resources and Redis.

## Approved personal-subscription verification

- Approved temporary group: `elwood-m6-rg`, region `westeurope`; approved on 8 October 2026 with a **€10 total allowance**. Ask the owner before exceeding it. This is not an enforced Azure spending cap.
- Scope: a **new** group, with only Elwood synthetic tests. Existing production groups/resources are out of scope.
- Initial storage target: <= 1 GiB and <= 100,000 requests; expected well below €1/month, excluding tax. Delete the test account after verification or explicitly approve keeping it. Versioned/soft-deleted bytes remain billable until reclaimed.
- This file creates neither the group nor resources until explicitly deployed. Confirm the group and costs with the owner first. If that group already exists, stop and inspect its ownership; do not reuse it silently.
- The CLI account was verified and .NET 10 isolated Functions is available in West Europe. Microsoft.Storage was registered for the approved lab deployment; Event Grid has not been provisioned.

## Commands after approval

Use the selected subscription explicitly in every Azure command. These examples intentionally have placeholders rather than embedding a personal subscription, tenant, email or principal ID in a public repository.

```powershell
$subscription = '<approved-subscription-id>'
$group = 'elwood-m6-rg'
$location = 'westeurope'
az account show --subscription $subscription --query '{name:name,id:id,tenantId:tenantId}'
# If authentication has expired, the owner signs in in their own terminal.
az group exists --name $group --subscription $subscription
# Continue only if false and this exact group was approved.
az provider register --namespace Microsoft.Storage --subscription $subscription --wait
az group create --name $group --location $location --subscription $subscription --tags project=elwood purpose=m6-contract-tests
$operatorId = az ad signed-in-user show --query id -o tsv
az deployment group what-if --resource-group $group --subscription $subscription --template-file infra/azure-small/test-storage.bicep --parameters operatorPrincipalId=$operatorId
# Review the changes before the following command. They must be limited to the new group.
az deployment group create --name elwood-m6-test-storage --resource-group $group --subscription $subscription --template-file infra/azure-small/test-storage.bicep --parameters operatorPrincipalId=$operatorId
```

Every command result/error must be checked before continuing. Do not print access tokens, storage keys or service-principal credentials. If the operator identity cannot be resolved, ask the owner; do not initiate an interactive login. Data-plane role propagation can take time; readiness must verify actual authorized data operations.

## Verification this enables

Cloud adapter contract tests must prove lease acquisition/renewal/loss, rejection of stale conditional writes, concurrent duplicate acceptance, corrupt-record isolation, indexed due-work/expiry queries, durable queues, restartable deletion and version-safe output cleanup. A successful template deployment is not evidence that these behaviors work.

Three opt-in live Blob service probes are supplied (adapter contract tests are additional coverage):

```powershell
$env:ELWOOD_M6_BLOB_URL = 'https://<approved-elwoodm6-account>.blob.core.windows.net/'
dotnet test dotnet/tests/Elwood.Pipeline.Azure.Tests --filter FullyQualifiedName~M6StorageServiceTests
```

They refuse non-lab account URLs, use the signed-in CLI identity, generate unique synthetic blob names, check lease exclusion and stale writes after both forced break and natural expiry, and check ETag-safe/version-specific deletion. They then remove their own versions and verify none remain live. With no endpoint configured they are explicitly skipped, not counted as passing live tests. Cleanup failures fail the test; soft-deleted bytes still follow the account's finite retention window. Lifecycle deletion is asynchronous; an abandoned object can pass through the seven-day lifecycle age and then seven-day soft-delete window, so this is not a seven-day physical erasure guarantee.

The test harness must limit generated data and request counts, delete its queue/table rows and Blob versions, and report any failed cleanup. Blob lifecycle does not clean Table entities or guarantee immediate deletion. Check the account's actual retained bytes before leaving it allocated.

The durable adapters, shared catalog and native Functions preview are now implemented and verified below. The production profile still needs delivered-output cleanup, quotas, monitoring, installed lifecycle checks, live Event Grid verification and the full hosted integration campaign. Production retention remains 30 days by default, configurable per pipeline/destination. **Never copy this lab's seven-day blanket rule onto production execution or business-output containers.**

See [M6 Azure design and estimate](../../docs/m6-azure-profile.md).

## Removing the temporary lab

Keep this group only while the M6 verification campaign needs it. After verification, check the group tags and resource inventory against the lab deployment, then delete this exact group in the approved subscription. Do not delete by wildcard, reuse a production group or unregister the subscription-wide Storage provider.

```powershell
az group show --name elwood-m6-rg --subscription $subscription --query '{name:name,tags:tags}'
az resource list --resource-group elwood-m6-rg --subscription $subscription --query '[].{name:name,type:type}'
# Only after confirming that all resources still belong to this disposable lab:
az group delete --name elwood-m6-rg --subscription $subscription
az group exists --name elwood-m6-rg --subscription $subscription
# Expected: false. A failed deletion must be investigated, not treated as cleanup success.
```

The cost allowance covers the entire campaign, including storage left allocated between runs. Check resource-group-scoped Cost Management data before adding hosting or other meters; allow for billing delay rather than interpreting missing rows as zero cost.

## Temporary Functions verification

`test-runtime.bicep` adds a Flex Consumption .NET 10 isolated app to this **existing synthetic lab only**. It uses the existing account, an account-scoped managed identity, 512 MiB on-demand instances, no always-ready capacity and no Redis. It creates separate preview catalog/package containers. This is not a production installer: the lab's seven-day execution-data rule is incompatible with the production 30-day contract. The runtime explicitly requires `Elwood:VerificationOnly=true`; `/api/health` is liveness, while `/api/readiness` returns 503 until cloud retention and release verification are complete.

Prepare a random management token in a private, ignored local file and supply only its SHA-256 hash as the secure `managementTokenSha256` parameter. Never print or commit the bearer token. Preview with `az deployment group what-if`, deploy only to the approved temporary group, publish the Functions project in Release, ZIP the **contents** of the publish folder (including `host.json`, `functions.metadata` and `.azurefunctions`), then deploy with `az functionapp deployment source config-zip`. SCM/FTP basic publishing is disabled; the operator uses Azure CLI authentication. No service-principal password is needed.

Opt-in hosted smoke test, after the package deployment succeeds:

```powershell
$env:ELWOOD_M6_HOST_URL = 'https://<approved-elwood-m6-api-name>.azurewebsites.net/'
$env:ELWOOD_M6_HOST_TOKEN_FILE = (Resolve-Path '.private/m6-host-token').Path
node --test scripts/m6-hosted.test.mjs
```

The test verifies management authentication, unpublished drafts, explicit mock testing, deployment, durable queue completion, duplicate acceptance, inspection and replay of the original revision after redeployment. Its execution TTL is five minutes; the independent retention function runs every five minutes. It retires its pipeline afterwards. Confirm removal of execution data separately; retired catalog entries and deployment packages remain until this temporary lab is removed. Stop the temporary function app between verification sessions and remove the group after the campaign.

The hosted runtime processes one bounded due-index page each minute and one expiry page every five minutes. Queue messages use Base64, four-message batches, five transport attempts and a one-minute retry visibility delay. Poison hints are consumed without retaining/logging their bodies; authoritative work remains in execution state. Recovery/retention use separate Functions timers and do not depend on a permanently running application loop. This small profile deliberately rejects RabbitMQ queue deployment until a separately provisioned always-running ingress is configured.

West Europe EUR retail meters checked on 8 October 2026: paid on-demand compute EUR 0.000023 per GB-second, paid executions EUR 0.000004 per ten. Thus 20,000 GB-seconds costs EUR 0.46 and 10,000 invocations EUR 0.004 before available subscription-wide grants, tax or storage. The Retail Prices API's legacy output rounded tiny values; the preview API and full-precision values were used. Actual group cost before compute was EUR 0.0000201126, with billing delay. The approved total campaign allowance remains EUR 10. Sources: [Azure Retail Prices API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices), [Functions pricing](https://azure.microsoft.com/en-us/pricing/details/functions/).
