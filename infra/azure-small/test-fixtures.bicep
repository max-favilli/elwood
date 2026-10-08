targetScope = 'resourceGroup'
@allowed([true])
param syntheticOnly bool = true
param location string = resourceGroup().location
param sqlLocation string = location
param operatorPrincipalId string
param operatorIp string
var suffix = uniqueString(resourceGroup().id)
var tags = { project: 'elwood', purpose: 'm6-disposable-fixtures', syntheticOnly: string(syntheticOnly) }
resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: 'elwoodm6${suffix}'
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false, publicNetworkAccess: 'Enabled' }
}
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'elwood-m6-ingress-plan'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: 'B1', tier: 'Basic', capacity: 1 }
  properties: { reserved: true }
}
resource sql 'Microsoft.Sql/servers@2023-08-01' = {
  name: 'elwood-m6-sql-${suffix}'
  location: sqlLocation
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: { administratorType: 'ActiveDirectory', principalType: 'User', login: 'm6-operator', sid: operatorPrincipalId, tenantId: subscription().tenantId, azureADOnlyAuthentication: true }
  }
}
resource db 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sql
  name: 'fixtures'
  location: sqlLocation
  sku: { name: 'Basic', tier: 'Basic', capacity: 5 }
  properties: { maxSizeBytes: 2147483648, requestedBackupStorageRedundancy: 'Local' }
}
// Disposable synthetic database only. Production deployments must narrow networking.
resource azure 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sql
  name: 'SyntheticAzureClients'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}
resource operator 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sql
  name: 'Operator'
  properties: { startIpAddress: operatorIp, endIpAddress: operatorIp }
}
output registryName string = registry.name
output sqlHost string = sql.properties.fullyQualifiedDomainName
output planId string = plan.id
