# Native Azure installation

`capacityProfile: files-25m` selects the large-file profile (4 GiB workers and partitioned retention), verified with three simultaneous 25 MiB generated Deldo fixtures. The default remains `guarded`. Read the [measured capacity and cost basis](../../docs/capacity-profile.md); switching a populated installation requires an explicit offline migration. Permanent hosting cost approval and real supplier/database acceptance remain pending.

The profile runs .NET 10 isolated Azure Functions on Flex Consumption, using Blob checkpoints/documents/catalog, Table scheduling indexes and Storage Queue notifications. Set `queueProvider: serviceBus` for a Basic Service Bus internal queue. External RabbitMQ requires a separately billed always-running ingress host. Redis and SQL are not runtime dependencies; the lab's SQL is only a connector fixture.

Read [operations and limits](../../docs/azure-operations.md), [verification evidence](../../docs/m6-release-evidence.md) and [the master checklist](../../docs/master-checklist.md) before adoption. Azure is the first supported native provider; AWS/GCP adapters are not supplied. Controlled fixtures do not certify the full pet-project workload.

## Templates

- `main.bicep`: new installation in a dedicated approved resource group.
- `storage.bicep`: Entra-only Standard LRS Blob/Queue/Table with authenticated public endpoints and lifecycle policy.
- `runtime.bicep`: Flex host, identity access, CORS and secure settings; can also use already provisioned storage.
- `service-bus.bicep`: optional Basic namespace/queue and scoped roles.
- `ingress.bicep`: optional Linux B1 always-on RabbitMQ ingress. Publish its application separately.
- `retention-policy.bicep`: owned-data backstop and bounded versions/snapshots.

Use a dedicated account. Data roles cover that account; pipeline secret, SQL-server and Blob-prefix grants impose additional application restrictions. A custom role grants Blob Data Contributor actions plus `deleteBlobVersion/action`, without Blob Data Owner's ACL ownership actions. Service Bus uses queue-scoped Sender and Receiver roles. Reader permits actual lifecycle verification. Shared keys, anonymous Blob access and SCM/FTP basic publishing are disabled. Upgrades must explicitly remove old Blob/Service Bus Data Owner assignments: ARM incremental deployment does not remove historical assignments.

Storage and Basic Service Bus deliberately retain public TLS endpoints, authenticated with managed identity. Storage `networkAcls` explicitly allow the public network; this is **not private networking**. Disabling public access without VNet integration, private endpoints and DNS would break the host. Service Bus private endpoints require Premium, outside this low-cost profile. Consumers requiring private networking must cost and validate that separate configuration before deployment. [Service Bus networking](https://learn.microsoft.com/en-us/azure/service-bus-messaging/secure-service-bus).

Templates provision infrastructure, not binaries. No SQL database, Redis, portal server or deployment service-principal password is created. Service Bus and RabbitMQ ingress are optional. Application Insights, a Log Analytics workspace (30-day retention, provisional 0.1 GB/day ingestion cap), and a five-minute retention alert are included. Monitoring/alert charges are additional to the historical capacity estimate and require consumer cost approval. The ingestion cap is not a spending cap and can overshoot. **A capped workspace stops the retention heartbeat from reaching the alert and can produce a false retention alarm**, even while cleanup succeeds. Check authenticated `/api/retention` and workspace ingestion status before diagnosing that alert. Supply existing `retentionAlertActionGroups` to deliver notifications; an empty list creates an Azure alert with no notification recipients.

Worker adaptive sampling targets two items/second, excluding traces and exceptions so retention health signals remain observable. Automatic SDK/HTTP dependency tracking is disabled: routine storage maintenance must not consume the workspace allowance. Host telemetry keeps its own sampling. Before sizing a consumer workspace, measure a complete day of `_BilledSize` by telemetry type, including scheduled maintenance and representative traffic, and allow headroom for bursts. The disposable install's daily measurement and limitations are recorded with the release evidence; 0.1 GB/day is provisional until that measurement completes.

The default `maximumInstanceCount` is 3, with no always-ready allocation. Azure applies this ceiling per independently scaling function group, not as a total app or euro cap. Approve a different ceiling for larger bursts. [Flex scale limits](https://learn.microsoft.com/en-us/azure/azure-functions/flex-consumption-plan#maximum-instance-count-the-ceiling).

## Installation

Use .NET 10 SDK and Azure CLI/Bicep. Obtain approval for the subscription, resource group, region and costs. West Europe was verified. If CLI authentication expires, the owner signs in; automation must not initiate an interactive login.

The deployer needs `Microsoft.Authorization/roleDefinitions/write` **at the resource group**, plus `Microsoft.Authorization/roleAssignments/write` for the grants. Contributor alone does not provide these permissions. The custom role is scoped to this group's storage, not to unrelated resources.

Create an ignored private parameter file containing a random management bearer token's SHA-256 hash, exact `portalOrigins`, and optional `additionalSettings`. Keep the bearer token outside Git. Additional settings use the secure shape `{"values":[{"name":"Elwood__SecretGrants__pipeline-id__Run__0","value":"supplier.password"}]}`. Trusted operator settings can override template defaults. Secret values use names such as `ELWOOD_SECRET_SUPPLIER_PASSWORD`. Test and Run grants are separate.

Prefer `secretReferences: [{"name":"ELWOOD_SECRET_SUPPLIER_PASSWORD","secretUri":"https://<vault>.vault.azure.net/secrets/supplier-password"}]` over literal secrets. Both hosts set `keyVaultReferenceIdentity` to the runtime identity and materialize an App Service Key Vault reference. Grant that identity **Key Vault Secrets User** on the consumer's selected vault/secrets and allow network access; vault provisioning and grants remain the consumer's responsibility. Never put a Key Vault reference inside `ELWOOD_RUNTIME_CONFIG_JSON`: references must occupy individual app settings. [Reference syntax and rotation](https://learn.microsoft.com/en-us/azure/app-service/app-service-key-vault-references).

```powershell
$subscription = '<approved-subscription>'
$group = '<approved-new-group>'
az account show --subscription $subscription --query '{name:name,id:id,tenantId:tenantId}'
az group exists --name $group --subscription $subscription
# Inspect existing groups; create only an explicitly approved new group.
az group create --name $group --location westeurope --subscription $subscription
az deployment group what-if --resource-group $group --subscription $subscription --template-file infra/azure-small/main.bicep --parameters '@.private/install.parameters.json'
az deployment group create --name elwood-install --resource-group $group --subscription $subscription --template-file infra/azure-small/main.bicep --parameters '@.private/install.parameters.json'
dotnet publish dotnet/src/Elwood.Runtime.Azure.Functions -c Release -o .private/functions
```

Before uploading binaries/starting any workers, initialize a **fresh, empty namespace** explicitly:

```powershell
./scripts/initialize-azure-retention.ps1 -SubscriptionId $subscription -StorageAccount '<deployment-output-storageAccount>' -CapacityProfile files-25m
```

Use the same `CapacityProfile` as the deployment (`guarded` is the default). The signed-in operator needs Blob data access on this account. The script refuses existing current/deleted/version data, writes ledgers with create-only preconditions, and writes the layout marker last. A partial bootstrap must be investigated before selecting a fresh namespace or resuming manually; do not overwrite it. A missing ledger on an existing installation requires restoring control data, never rerunning bootstrap. The runtime will neither infer an empty ledger nor auto-initialize. Readiness stays 503 until the first successful retention sweep (normally within five minutes).

Check each result. ZIP the contents of the publish folder, including `.azurefunctions`, `functions.metadata` and `host.json`; deploy with `az functionapp deployment source config-zip`, using Entra/CLI authentication. Flex overwrites one current `released-package.zip`; old versions have seven-day lifecycle retention. Keep a bounded local set of prior release packages. [Microsoft deployment documentation](https://learn.microsoft.com/en-us/azure/azure-functions/functions-infrastructure-as-code).

Set portal `NEXT_PUBLIC_API_URL` to the returned HTTPS API URL before building. Rebuild when changing it. Allow its exact origin and sign in with the bearer token. Management authentication does not authenticate trigger calls; declare trigger authentication in YAML. Check `/api/health`, `/api/readiness` and authenticated `/api/retention`, then validate/mock-test/save/explicitly deploy a pipeline. Infrastructure success alone does not prove integration behavior.

App Service rejects hyphens in individual setting names during some updates. Pipeline-keyed `Elwood:*` settings therefore live in secure `ELWOOD_RUNTIME_CONFIG_JSON`; native Functions binding settings and `ELWOOD_SECRET_*` remain separate. Never print this JSON. Change settings through the private parameter file.

For RabbitMQ, approve B1 cost before enabling `enableRabbitMqIngress`, publish/ZIP `dotnet/src/Elwood.Runtime.Azure.Ingress` and deploy to the generated ingress app. It shares the catalog, identity, state and internal queue with Functions. Give it broker network access and scoped secrets. `/health` is process liveness; `/readiness` checks storage retention, not broker connectivity. Monitor broker connection failures separately.

For BlobCreated events, configure Event Grid against the deployed HTTP trigger with `eventGrid: true`, scoped subject filters and secret delivery authentication. `test-event-grid.bicep` illustrates the authenticated validation/delivery contract. Event identity suppresses redelivery; opt-in [content deduplication](../../docs/content-deduplication.md) additionally suppresses different events carrying equal file bytes.

## Disposable verification lab

`test-*` templates and `fixture.*` are synthetic infrastructure, not production installers. Never copy `test-storage.bicep`'s historical seven-day blanket rule onto production data. The campaign replaced it with `retention-policy.bicep` before retention verification. The combined RabbitMQ/HTTP fixture container is disposable and has no production broker durability promise.

The approved temporary group was `elwood-m6-rg`, West Europe, with a €10 total campaign allowance. Opt-in scripts naming it can stop/restart apps and change synthetic pipelines. Live service tests use guarded lab endpoints, Azure CLI identity and unique namespaces. Missing opt-in configuration means skipped tests, not live passes.

After verification, inspect the exact group's tags/inventory, delete only that approved group, and require `az group exists` to return `false`. Never delete by wildcard or unregister subscription-wide providers. Billing is delayed; include resources left allocated between runs when checking the allowance.
