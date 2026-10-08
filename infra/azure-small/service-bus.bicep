targetScope = 'resourceGroup'
param namespaceName string
param location string = resourceGroup().location
param queueName string = 'execution-work'
param runtimePrincipalId string
param operatorPrincipalId string = ''

resource broker 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: namespaceName
  location: location
  sku: { name: 'Basic', tier: 'Basic' }
  properties: { minimumTlsVersion: '1.2', disableLocalAuth: true, publicNetworkAccess: 'Enabled' }
}
resource work 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: broker
  name: queueName
  properties: {
    lockDuration: 'PT1M'
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P7D'
    deadLetteringOnMessageExpiration: false
    maxDeliveryCount: 5
    requiresDuplicateDetection: false
    requiresSession: false
    enablePartitioning: false
  }
}
var dataRole = '090c5cfd-751d-490a-894a-3ce6f1109419'
resource runtimeAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(work.id, runtimePrincipalId, dataRole)
  scope: work
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', dataRole)
    principalId: runtimePrincipalId
    principalType: 'ServicePrincipal'
  }
}
resource operatorAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(operatorPrincipalId)) {
  name: guid(work.id, operatorPrincipalId, dataRole)
  scope: work
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', dataRole)
    principalId: operatorPrincipalId
    principalType: 'User'
  }
}
output fullyQualifiedNamespace string = '${broker.name}.servicebus.windows.net'
output workQueue string = work.name
