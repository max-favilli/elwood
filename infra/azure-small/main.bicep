targetScope = 'resourceGroup'
@minLength(3)
@maxLength(18)
@description('Lowercase letters/numbers/hyphens; use a dedicated resource group.')
param project string = 'elwood'
param location string = resourceGroup().location
@secure()
@minLength(64)
@maxLength(64)
param managementTokenSha256 string
param portalOrigins array = []
@allowed(['storage', 'serviceBus'])
param queueProvider string = 'storage'
@allowed(['guarded', 'files-25m'])
@description('files-25m reserves 4 GiB per active worker and uses partitioned retention accounting. Requires a fresh installation or explicit offline migration.')
param capacityProfile string = 'guarded'
@description('Creates a separately billed always-running B1 ingress host for external RabbitMQ.')
param enableRabbitMqIngress bool = false
@secure()
@description('Object with values:[{name,value}] for trusted-operator secret and connector grants. No sample secrets are installed.')
param additionalSettings object = {}
var suffix = uniqueString(resourceGroup().id)
var accountName = 'elw${suffix}'
var apiName = '${project}-api-${suffix}'
var busName = '${project}-bus-${suffix}'
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${project}-runtime-identity'
  location: location
}
module storage 'storage.bicep' = {
  name: '${project}-storage'
  params: { accountName: accountName, location: location }
}
module broker 'service-bus.bicep' = if (queueProvider == 'serviceBus') {
  name: '${project}-servicebus'
  params: { namespaceName: busName, location: location, runtimePrincipalId: identity.properties.principalId }
}
module runtime 'runtime.bicep' = {
  name: '${project}-runtime'
  params: {
    location: location
    accountName: storage.outputs.name
    appName: apiName
    identityName: identity.name
    planName: '${project}-flex'
    managementTokenSha256: managementTokenSha256
    portalOrigins: portalOrigins
    queueProvider: queueProvider
    capacityProfile: capacityProfile
    serviceBusNamespace: queueProvider == 'serviceBus' ? '${busName}.servicebus.windows.net' : 'unused.servicebus.windows.net'
    enableRabbitMqIngress: enableRabbitMqIngress
    additionalSettings: additionalSettings
  }
  dependsOn: [broker]
}
output apiUrl string = runtime.outputs.baseUrl
output appName string = apiName
output storageAccount string = accountName
output identityClientId string = identity.properties.clientId
output identityPrincipalId string = identity.properties.principalId
