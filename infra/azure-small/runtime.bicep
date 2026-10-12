targetScope = 'resourceGroup'

param location string = resourceGroup().location
param accountName string
param appName string
param identityName string
param planName string
param stateContainer string = 'state'
param documentContainer string = 'documents'
param outputContainer string = 'outputs'
param controlContainer string = 'control'
param packageContainer string = 'packages'
param indexTable string = 'ExecutionIndex'
param workQueue string = 'execution-work'
param storageNamespace string = 'elwood'
param verificationOnly bool = false
param enableRabbitMqIngress bool = false
param managementName string = 'operator'
param portalOrigins array
@secure()
@minLength(64)
@maxLength(64)
param managementTokenSha256 string
@allowed(['storage', 'serviceBus'])
param queueProvider string = 'storage'
@allowed(['guarded', 'files-25m'])
param capacityProfile string = 'guarded'
@minValue(1)
@maxValue(1000)
param maximumInstanceCount int = 3
param retentionAlertActionGroups array = []
param secretReferences array = []
param serviceBusNamespace string = 'unused.servicebus.windows.net'
@secure()
param additionalSettings object = {}
var tags = { project: 'elwood', profile: 'azure-small' }
var largeFiles = capacityProfile == 'files-25m'

resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' existing = { name: accountName }
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' existing = { parent: storage, name: 'default' }
resource queues 'Microsoft.Storage/storageAccounts/queueServices@2025-06-01' existing = { parent: storage, name: 'default' }
resource catalog 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  parent: blobs
  name: controlContainer
  properties: { publicAccess: 'None' }
}
resource packages 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  parent: blobs
  name: packageContainer
  properties: { publicAccess: 'None' }
}
resource poison 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-06-01' = {
  parent: queues
  name: '${workQueue}-poison'
  properties: {}
}
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: identityName }
var dataRoles = [
  'acdd72a7-3385-48ef-bd42-f606fba81ae7' // Reader verifies actual lifecycle and soft-delete settings.
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
]
resource blobRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'elwood-blob-contributor-with-version-delete')
  properties: {
    roleName: 'Elwood Blob Contributor and version cleanup ${uniqueString(resourceGroup().id)}'
    description: 'Storage Blob Data Contributor permissions plus deletion of owned blob versions; no ACL ownership actions.'
    type: 'CustomRole'
    assignableScopes: [resourceGroup().id]
    permissions: [{
      actions: [
        'Microsoft.Storage/storageAccounts/blobServices/containers/delete'
        'Microsoft.Storage/storageAccounts/blobServices/containers/read'
        'Microsoft.Storage/storageAccounts/blobServices/containers/write'
        'Microsoft.Storage/storageAccounts/blobServices/generateUserDelegationKey/action'
      ]
      dataActions: [
        'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete'
        'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'
        'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write'
        'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/move/action'
        'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/add/action'
        'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/deleteBlobVersion/action'
      ]
    }]
  }
}
resource blobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, blobRole.id)
  scope: storage
  properties: { roleDefinitionId: blobRole.id, principalId: identity.properties.principalId, principalType: 'ServicePrincipal' }
}
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${appName}-logs'
  location: location
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30, workspaceCapping: { dailyQuotaGb: json('0.1') } }
}
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${appName}-insights'
  location: location
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id }
}
resource access 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in dataRoles: {
  name: guid(storage.id, identity.id, role)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}
var originSettings = [for (origin, i) in portalOrigins: { name: 'Elwood__Management__Origins__${i}', value: origin }]
var baseSettings = concat([
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: identity.properties.clientId }
        { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
        { name: 'ElwoodQueue__queueServiceUri', value: storage.properties.primaryEndpoints.queue }
        { name: 'ElwoodQueue__credential', value: 'managedidentity' }
        { name: 'ElwoodQueue__clientId', value: identity.properties.clientId }
        { name: 'ElwoodWorkQueue', value: workQueue }
        { name: 'ElwoodServiceBusQueue', value: workQueue }
        { name: 'ElwoodServiceBus__fullyQualifiedNamespace', value: serviceBusNamespace }
        { name: 'ElwoodServiceBus__credential', value: 'managedidentity' }
        { name: 'ElwoodServiceBus__clientId', value: identity.properties.clientId }
        { name: 'Elwood__Azure__QueueProvider', value: queueProvider }
        { name: 'Elwood__Azure__CapacityProfile', value: capacityProfile }
        { name: 'DOTNET_GCHeapHardLimitPercent', value: largeFiles ? '0x55' : '0x4B' } // Large transforms need margin beyond the standalone 3 GiB probe.
        { name: 'DOTNET_GCConserveMemory', value: largeFiles ? '9' : '0' }
        { name: 'AzureFunctionsJobHost__extensions__queues__batchSize', value: largeFiles ? '1' : '4' }
        { name: 'AzureFunctionsJobHost__extensions__queues__newBatchThreshold', value: largeFiles ? '0' : '2' }
        { name: 'AzureFunctionsJobHost__extensions__serviceBus__maxConcurrentCalls', value: largeFiles ? '1' : '4' }
        { name: 'AzureWebJobs.ServiceBusExecutionStep.Disabled', value: string(queueProvider != 'serviceBus') }
        { name: 'AzureWebJobs.ServiceBusDeadLetters.Disabled', value: string(queueProvider != 'serviceBus') }
        { name: 'AzureWebJobs.ExecutionStep.Disabled', value: string(queueProvider != 'storage') }
        { name: 'AzureWebJobs.ExecutionPoisonHint.Disabled', value: string(queueProvider != 'storage') }
        { name: 'Elwood__Azure__BlobServiceUri', value: storage.properties.primaryEndpoints.blob }
        { name: 'Elwood__Azure__TableServiceUri', value: storage.properties.primaryEndpoints.table }
        { name: 'Elwood__Azure__QueueServiceUri', value: storage.properties.primaryEndpoints.queue }
        { name: 'Elwood__Azure__StateContainer', value: stateContainer }
        { name: 'Elwood__Azure__DocumentContainer', value: documentContainer }
        { name: 'Elwood__Azure__CatalogContainer', value: catalog.name }
        { name: 'Elwood__Azure__IndexTable', value: indexTable }
        { name: 'Elwood__Azure__Namespace', value: storageNamespace }
        { name: 'Elwood__Azure__StorageResourceId', value: storage.id }
        { name: 'Elwood__Azure__OutputContainer', value: outputContainer }
        { name: 'Elwood__Retention__Enforced', value: 'true' }
        { name: 'Elwood__Retention__BlobTargets__0__AccountUrl', value: storage.properties.primaryEndpoints.blob }
        { name: 'Elwood__Retention__BlobTargets__0__Container', value: outputContainer }
        { name: 'Elwood__Retention__BlobTargets__0__Prefix', value: storageNamespace }
        { name: 'Elwood__Management__BehindProxy', value: 'true' }
        { name: 'Elwood__Management__LocalDevelopment', value: 'false' }
        { name: 'Elwood__Management__Tokens__0__Name', value: managementName }
        { name: 'Elwood__Management__Tokens__0__TokenSha256', value: managementTokenSha256 }
        { name: 'Elwood__Management__Tokens__0__Permissions__0', value: 'read' }
        { name: 'Elwood__Management__Tokens__0__Permissions__1', value: 'write' }
        { name: 'Elwood__Management__Tokens__0__Permissions__2', value: 'test' }
        { name: 'Elwood__Management__Tokens__0__Permissions__3', value: 'deploy' }
        { name: 'Elwood__Management__Tokens__0__Permissions__4', value: 'recover' }
        { name: 'Elwood__Management__Tokens__0__Pipelines__0', value: '*' }
        { name: 'Elwood__Sync__ExecutionBudgetSeconds', value: '60' }
        { name: 'Elwood__VerificationOnly', value: string(verificationOnly) }
        { name: 'Elwood__RabbitMqIngressEnabled', value: string(enableRabbitMqIngress) }
      ], largeFiles ? [{ name: 'DOTNET_GCHeapHardLimit', value: '0xD9999999' }] : [], originSettings) // Explicit 3.4 GiB ceiling, independent of host-reported physical memory.
var keyVaultSettings = [for secret in secretReferences: { name: secret.name, value: '@Microsoft.KeyVault(SecretUri=${secret.secretUri})' }]
var extraSettings = concat(additionalSettings.?values ?? [], keyVaultSettings)
var allSettings = concat(filter(baseSettings, s => !contains(map(extraSettings, extra => extra.name), s.name)), extraSettings)

// App Service settings updates reject hyphens in keys. Keep binding settings
// native and encode Elwood's pipeline-keyed configuration in one secure setting.
var runtimeSettings = concat(filter(allSettings, s => !startsWith(s.name, 'Elwood__')), [
  { name: 'ELWOOD_RUNTIME_CONFIG_JSON', value: string(toObject(filter(allSettings, s => startsWith(s.name, 'Elwood__')), s => replace(s.name, '__', ':'), s => s.value)) }
])

resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identity.id
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      cors: { allowedOrigins: portalOrigins, supportCredentials: false }
      appSettings: runtimeSettings
    }
    functionAppConfig: {
      runtime: { name: 'dotnet-isolated', version: '10.0' }
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${packages.name}'
          authentication: { type: 'UserAssignedIdentity', userAssignedIdentityResourceId: identity.id }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: largeFiles ? 4096 : 512
        triggers: { http: { perInstanceConcurrency: largeFiles ? 1 : 2 } }
      }
    }
  }
  dependsOn: [access, blobAccess, poison]
}
resource retentionAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${appName}-retention-health'
  location: location
  properties: {
    displayName: 'Elwood retention unhealthy or heartbeat absent'
    description: 'Retention failed or no healthy sweep was observed in 15 minutes. Pause admission and inspect /api/retention.'
    enabled: true
    severity: 1
    scopes: [logs.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    skipQueryValidation: true // AppTraces may not exist until the first package runs.
    criteria: { allOf: [{
      query: 'AppTraces | where AppRoleName == "${appName}" | where Message startswith "ELWOOD_RETENTION_" | summarize Good=countif(Message == "ELWOOD_RETENTION_HEALTHY"), Bad=countif(Message startswith "ELWOOD_RETENTION_UNHEALTHY") | where Good == 0 or Bad > 0'
      timeAggregation: 'Count'
      operator: 'GreaterThan'
      threshold: 0
      failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
    }] }
    autoMitigate: true
    actions: { actionGroups: retentionAlertActionGroups }
  }
}
resource scmBasic 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: app
  name: 'scm'
  properties: { allow: false }
}
resource ftpBasic 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: app
  name: 'ftp'
  properties: { allow: false }
}
output appName string = app.name
output baseUrl string = 'https://${app.properties.defaultHostName}'
output identityId string = identity.id


module ingress 'ingress.bicep' = if (enableRabbitMqIngress) {
  name: 'elwood-rabbitmq-ingress'
  params: { appName: '${appName}-ingress', location: location, identityName: identityName, settings: { values: allSettings } }
  dependsOn: [access, blobAccess]
}
