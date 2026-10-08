targetScope = 'resourceGroup'

@description('Temporary synthetic verification only. Requires test-storage.bicep in the same approved lab group.')
@allowed([true])
param testOnly bool = true
param location string = resourceGroup().location

@secure()
@minLength(64)
@maxLength(64)
param managementTokenSha256 string

var suffix = uniqueString(resourceGroup().id)
var accountName = 'elwoodm6${suffix}'
var appName = 'elwood-m6-api-${suffix}'
var tags = { project: 'elwood', purpose: 'm6-host-verification', dataClassification: 'synthetic-only' }

resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' existing = { name: accountName }
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' existing = { parent: storage, name: 'default' }
resource queues 'Microsoft.Storage/storageAccounts/queueServices@2025-06-01' existing = { parent: storage, name: 'default' }
resource catalog 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  parent: blobs
  name: 'preview-catalog'
  properties: { publicAccess: 'None' }
}
resource packages 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  parent: blobs
  name: 'preview-packages'
  properties: { publicAccess: 'None' }
}
resource poison 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-06-01' = {
  parent: queues
  name: 'contract-work-poison'
  properties: {}
}
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'elwood-m6-runtime-identity'
  location: location
  tags: tags
}
var dataRoles = [
  'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' // Blob Data Owner includes version deletion.
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
]
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
  name: 'elwood-m6-flex'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}
resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: identity.properties.clientId }
        { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
        { name: 'ElwoodQueue__queueServiceUri', value: storage.properties.primaryEndpoints.queue }
        { name: 'ElwoodQueue__credential', value: 'managedidentity' }
        { name: 'ElwoodQueue__clientId', value: identity.properties.clientId }
        { name: 'ElwoodWorkQueue', value: 'contract-work' }
        { name: 'Elwood__Azure__BlobServiceUri', value: storage.properties.primaryEndpoints.blob }
        { name: 'Elwood__Azure__TableServiceUri', value: storage.properties.primaryEndpoints.table }
        { name: 'Elwood__Azure__QueueServiceUri', value: storage.properties.primaryEndpoints.queue }
        { name: 'Elwood__Azure__StateContainer', value: 'contract-state' }
        { name: 'Elwood__Azure__DocumentContainer', value: 'contract-documents' }
        { name: 'Elwood__Azure__CatalogContainer', value: catalog.name }
        { name: 'Elwood__Azure__IndexTable', value: 'ContractIndex' }
        { name: 'Elwood__Azure__Namespace', value: 'hostpreview' }
        { name: 'Elwood__Management__BehindProxy', value: 'true' }
        { name: 'Elwood__Management__LocalDevelopment', value: 'false' }
        { name: 'Elwood__Management__Origins__0', value: 'http://localhost:3000' }
        { name: 'Elwood__Management__Tokens__0__Name', value: 'm6-test-operator' }
        { name: 'Elwood__Management__Tokens__0__TokenSha256', value: managementTokenSha256 }
        { name: 'Elwood__Management__Tokens__0__Permissions__0', value: 'read' }
        { name: 'Elwood__Management__Tokens__0__Permissions__1', value: 'write' }
        { name: 'Elwood__Management__Tokens__0__Permissions__2', value: 'test' }
        { name: 'Elwood__Management__Tokens__0__Permissions__3', value: 'deploy' }
        { name: 'Elwood__Management__Tokens__0__Permissions__4', value: 'recover' }
        { name: 'Elwood__Management__Tokens__0__Pipelines__0', value: '*' }
        { name: 'Elwood__Sync__ExecutionBudgetSeconds', value: '60' }
        { name: 'Elwood__VerificationOnly', value: string(testOnly) }
      ]
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
        maximumInstanceCount: 40 // Azure minimum; no always-ready instances.
        instanceMemoryMB: 512
        triggers: { http: { perInstanceConcurrency: 2 } }
      }
    }
  }
  dependsOn: [access, poison]
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
output verificationOnly bool = testOnly
