targetScope = 'resourceGroup'
param accountName string
param location string = resourceGroup().location
param stateContainer string = 'state'
param documentContainer string = 'documents'
param outputContainer string = 'outputs'
param controlContainer string = 'control'
param packageContainer string = 'packages'
param indexTable string = 'ExecutionIndex'
param workQueue string = 'execution-work'
resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: accountName
  location: location
  tags: { project: 'elwood', profile: 'azure-small' }
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  // Authenticated public endpoints: private networking is a separate hosting profile (see README).
  properties: { accessTier: 'Hot', minimumTlsVersion: 'TLS1_2', supportsHttpsTrafficOnly: true, allowBlobPublicAccess: false, allowSharedKeyAccess: false, defaultToOAuthAuthentication: true, publicNetworkAccess: 'Enabled', networkAcls: { defaultAction: 'Allow', bypass: 'AzureServices', ipRules: [], virtualNetworkRules: [] } }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' = {
  parent: storage
  name: 'default'
  properties: { isVersioningEnabled: true, deleteRetentionPolicy: { enabled: true, days: 7 }, containerDeleteRetentionPolicy: { enabled: true, days: 7 } }
}
resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = [for container in [stateContainer, documentContainer, outputContainer, controlContainer, packageContainer]: {
  parent: blobs
  name: container
  properties: { publicAccess: 'None' }
}]
resource queues 'Microsoft.Storage/storageAccounts/queueServices@2025-06-01' = { parent: storage, name: 'default', properties: {} }
resource work 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-06-01' = [for queue in [workQueue, '${workQueue}-poison']: { parent: queues, name: queue, properties: {} }]
resource tables 'Microsoft.Storage/storageAccounts/tableServices@2025-06-01' = { parent: storage, name: 'default', properties: {} }
resource index 'Microsoft.Storage/storageAccounts/tableServices/tables@2025-06-01' = { parent: tables, name: indexTable, properties: {} }
module lifecycle 'retention-policy.bicep' = {
  name: 'elwood-retention-policy'
  params: { accountName: storage.name, stateContainer: stateContainer, documentContainer: documentContainer, outputContainer: outputContainer, controlContainer: controlContainer, packageContainer: packageContainer }
  dependsOn: [containers]
}
output id string = storage.id
output name string = storage.name
