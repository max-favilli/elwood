targetScope = 'resourceGroup'

@description('Region for this isolated M6 test account. No existing resources are referenced.')
param location string = resourceGroup().location

@description('Object ID of the operator running storage contract tests. Obtain from the signed-in identity; never pass a client secret.')
@minLength(36)
@maxLength(36)
param operatorPrincipalId string

@description('This template is deliberately limited to disposable contract-test data.')
@allowed([true])
param testOnly bool = true

var accountName = 'elwoodm6${uniqueString(resourceGroup().id)}'
var tags = {
  project: 'elwood'
  purpose: 'm6-contract-tests'
  dataClassification: 'synthetic-only'
  managedBy: 'elwood-bicep'
}

resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: accountName
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowCrossTenantReplication: false
    // Authenticated SDK tests run from the maintainer's machine. No anonymous data access.
    publicNetworkAccess: 'Enabled'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' = {
  parent: storage
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = [for name in ['contract-state', 'contract-documents', 'contract-outputs']: {
  parent: blobService
  name: name
  properties: { publicAccess: 'None' }
}]

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2025-06-01' = {
  parent: storage
  name: 'default'
  properties: {}
}
resource queue 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-06-01' = {
  parent: queueService
  name: 'contract-work'
  properties: {}
}
resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2025-06-01' = {
  parent: storage
  name: 'default'
  properties: {}
}
resource table 'Microsoft.Storage/storageAccounts/tableServices/tables@2025-06-01' = {
  parent: tableService
  name: 'ContractIndex'
  properties: {}
}

// Lab backstop ONLY. Every object is synthetic/disposable. This is not the
// production policy: active recovery data and configurable output expiry need
// application cleanup plus a distinct verified lifecycle policy before release.
resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2025-06-01' = {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [{
        name: 'expire-disposable-contract-data'
        enabled: testOnly
        type: 'Lifecycle'
        definition: {
          filters: {
            blobTypes: ['blockBlob']
            prefixMatch: ['contract-state/', 'contract-documents/', 'contract-outputs/']
          }
          actions: {
            baseBlob: { delete: { daysAfterModificationGreaterThan: 7 } }
            snapshot: { delete: { daysAfterCreationGreaterThan: 7 } }
            version: { delete: { daysAfterCreationGreaterThan: 7 } }
          }
        }
      }]
    }
  }
}

// These data-plane roles apply only to this newly created test account.
var roles = [
  // Contributor cannot delete previous versions. Owner includes deleteBlobVersion/action,
  // required for these cleanup probes; this is a data role on this lab account only.
  'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' // Storage Blob Data Owner
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88' // Storage Queue Data Contributor
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' // Storage Table Data Contributor
]
resource testAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in roles: {
  name: guid(storage.id, operatorPrincipalId, role)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    principalId: operatorPrincipalId
    principalType: 'User'
  }
}]

output storageAccountName string = storage.name
output blobServiceUri string = storage.properties.primaryEndpoints.blob
output queueServiceUri string = storage.properties.primaryEndpoints.queue
output tableServiceUri string = storage.properties.primaryEndpoints.table
output disposableTestDataOnly bool = testOnly
