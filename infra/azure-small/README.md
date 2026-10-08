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

Three opt-in live Blob service probes are supplied (they do not yet prove the unimplemented M6 adapters):

```powershell
$env:ELWOOD_M6_BLOB_URL = 'https://<approved-elwoodm6-account>.blob.core.windows.net/'
dotnet test dotnet/tests/Elwood.Pipeline.Azure.Tests --filter FullyQualifiedName~M6StorageServiceTests
```

They refuse non-lab account URLs, use the signed-in CLI identity, generate unique synthetic blob names, check lease exclusion and stale writes after both forced break and natural expiry, and check ETag-safe/version-specific deletion. They then remove their own versions and verify none remain live. With no endpoint configured they are explicitly skipped, not counted as passing live tests. Cleanup failures fail the test; soft-deleted bytes still follow the account's finite retention window. Lifecycle deletion is asynchronous; an abandoned object can pass through the seven-day lifecycle age and then seven-day soft-delete window, so this is not a seven-day physical erasure guarantee.

The test harness must limit generated data and request counts, delete its queue/table rows and Blob versions, and report any failed cleanup. Blob lifecycle does not clean Table entities or guarantee immediate deletion. Check the account's actual retained bytes before leaving it allocated.

The production profile still needs Functions hosting, a shared management API/catalog, durable Table/Blob/Queue adapters, fencing in the runtime, independent cleanup, quotas, monitoring, installed lifecycle checks and live Event Grid verification. Production retention remains 30 days by default, configurable per pipeline/destination. **Never copy this lab's seven-day blanket rule onto production execution or business-output containers.**

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
