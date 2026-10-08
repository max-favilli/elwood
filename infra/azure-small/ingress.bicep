targetScope = 'resourceGroup'
param appName string
param location string = resourceGroup().location
param identityName string
@secure()
param settings object
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: identityName }
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${appName}-plan'
  location: location
  kind: 'linux'
  sku: { name: 'B1', tier: 'Basic', capacity: 1 }
  properties: { reserved: true }
}
var elwood = toObject(filter(settings.values, s => startsWith(s.name, 'Elwood')), s => replace(s.name, '__', ':'), s => s.value)
resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  kind: 'app,linux'
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      alwaysOn: true
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet Elwood.Runtime.Azure.Ingress.dll'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: concat(filter(settings.values, s => startsWith(s.name, 'ELWOOD_SECRET_')), [
        { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
        { name: 'ELWOOD_RUNTIME_CONFIG_JSON', value: string(elwood) }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
      ])
    }
  }
}
resource scm 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = { parent: app, name: 'scm', properties: { allow: false } }
resource ftp 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = { parent: app, name: 'ftp', properties: { allow: false } }
output host string = app.properties.defaultHostName
