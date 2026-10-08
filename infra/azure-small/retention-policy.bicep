targetScope = 'resourceGroup'

param accountName string
param stateContainer string = 'state'
param documentContainer string = 'documents'
param outputContainer string = 'outputs'
param controlContainer string = 'control'
param packageContainer string = 'packages'

resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' existing = { name: accountName }
resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2025-06-01' = {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'owned-artifact-backstop'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: ['blockBlob'], prefixMatch: ['${stateContainer}/', '${documentContainer}/', '${outputContainer}/'] }
            // Ordinary cleanup uses the pinned expiry ledger. This last-resort rule
            // is later than maximum execution (7d) + history (365d) + writer grace.
            actions: {
              baseBlob: { delete: { daysAfterModificationGreaterThan: 380 } }
              version: { delete: { daysAfterCreationGreaterThan: 380 } }
              snapshot: { delete: { daysAfterCreationGreaterThan: 380 } }
            }
          }
        }
        {
          name: 'control-history'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: ['blockBlob'], prefixMatch: ['${controlContainer}/', '${packageContainer}/', 'azure-webjobs-hosts/', 'azure-webjobs-secrets/'] }
            // Current definitions, cleanup instructions and the active package cannot
            // expire by age. Their counts/bytes are bounded separately.
            actions: {
              version: { delete: { daysAfterCreationGreaterThan: 7 } }
              snapshot: { delete: { daysAfterCreationGreaterThan: 7 } }
            }
          }
        }
        {
          name: 'abandoned-control-probes'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: ['blockBlob'], prefixMatch: ['${controlContainer}/health/'] }
            actions: {
              baseBlob: { delete: { daysAfterModificationGreaterThan: 1 } }
              version: { delete: { daysAfterCreationGreaterThan: 1 } }
              snapshot: { delete: { daysAfterCreationGreaterThan: 1 } }
            }
          }
        }
      ]
    }
  }
}
