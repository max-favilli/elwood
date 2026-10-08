targetScope = 'resourceGroup'
@secure()
param settings object
param imageTag string = 'm6-fixture:latest'
param location string = resourceGroup().location
var suffix = uniqueString(resourceGroup().id)
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: 'elwood-m6-runtime-identity' }
resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = { name: 'elwoodm6${suffix}' }
resource plan 'Microsoft.Web/serverfarms@2024-04-01' existing = { name: 'elwood-m6-ingress-plan' }
resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, 'pull')
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}
resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: 'elwood-m6-fixture-${suffix}'
  location: location
  kind: 'app,linux,container'
  tags: { project: 'elwood', purpose: 'm6-disposable-fixtures' }
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      alwaysOn: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      linuxFxVersion: 'DOCKER|${registry.properties.loginServer}/${imageTag}'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: identity.properties.clientId
      appSettings: concat(settings.values, [
        { name: 'WEBSITES_PORT', value: '8080' }
        { name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE', value: 'false' }
      ])
    }
  }
  dependsOn: [pull]
}
resource scm 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = { parent: app, name: 'scm', properties: { allow: false } }
resource ftp 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = { parent: app, name: 'ftp', properties: { allow: false } }
output host string = app.properties.defaultHostName
