targetScope = 'resourceGroup'
@description('Deploy only after the consumer approves the regional Redis cost. This template does not create application databases.')
param cacheName string
param location string = resourceGroup().location
@allowed(['Balanced_B0', 'Balanced_B1', 'Balanced_B3', 'Balanced_B5', 'Balanced_B10'])
param sku string = 'Balanced_B0'
@description('Enable for production availability/SLA. The cheaper false default is for development/test pilots; durable recovery does not prevent cache downtime.')
param highAvailability bool = false
@description('Existing RBAC-enabled Key Vault in this resource group. The deployer must be allowed to create its secret and role assignment.')
param keyVaultName string
param secretName string = 'elwood-redis'
@description('The Elwood runtime user-assigned managed identity principal ID, not its client ID.')
param runtimePrincipalId string

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }
resource cache 'Microsoft.Cache/redisEnterprise@2025-07-01' = {
  name: cacheName
  location: location
  sku: { name: sku }
  properties: {
    minimumTlsVersion: '1.2'
    highAvailability: highAvailability ? 'Enabled' : 'Disabled'
    // Matches the authenticated public networking of azure-small. Private
    // endpoints require consumer VNet/DNS wiring before disabling this path.
    publicNetworkAccess: 'Enabled'
  }
}
resource database 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' = {
  parent: cache
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    accessKeysAuthentication: 'Enabled'
    clusteringPolicy: 'NoCluster'
    evictionPolicy: 'NoEviction'
    port: 10000
    persistence: { aofEnabled: false, rdbEnabled: false }
  }
}
resource secret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: secretName
  properties: {
    value: '${cache.properties.hostName}:10000,password=${database.listKeys().primaryKey},ssl=True,abortConnect=False'
    contentType: 'Elwood Redis TLS connection'
  }
}
resource access 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, runtimePrincipalId, 'elwood-redis-secret-reader')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: runtimePrincipalId
    principalType: 'ServicePrincipal'
  }
}
output cacheResourceId string = cache.id
output connectionSecretUri string = '${vault.properties.vaultUri}secrets/${secret.name}'
