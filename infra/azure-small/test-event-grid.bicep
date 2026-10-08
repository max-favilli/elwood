targetScope = 'resourceGroup'
@secure()
param authorization string
param endpointUrl string
var suffix = uniqueString(resourceGroup().id)
resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' existing = { name: 'elwoodm6${suffix}' }
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' existing = { parent: storage, name: 'default' }
resource inputs 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = { parent: blobs, name: 'contract-inputs', properties: { publicAccess: 'None' } }
resource topic 'Microsoft.EventGrid/systemTopics@2022-06-15' = {
  name: 'elwood-m6-blob-events'
  location: resourceGroup().location
  properties: { source: storage.id, topicType: 'Microsoft.Storage.StorageAccounts' }
}
resource subscription 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2022-06-15' = {
  parent: topic
  name: 'deldo'
  properties: {
    eventDeliverySchema: 'EventGridSchema'
    filter: { includedEventTypes: ['Microsoft.Storage.BlobCreated'], subjectBeginsWith: '/blobServices/default/containers/contract-inputs/blobs/deldo/' }
    retryPolicy: { maxDeliveryAttempts: 5, eventTimeToLiveInMinutes: 30 }
    destination: {
      endpointType: 'WebHook'
      properties: {
        endpointUrl: endpointUrl
        maxEventsPerBatch: 1
        deliveryAttributeMappings: [{ name: 'Authorization', type: 'Static', properties: { value: authorization, isSecret: true } }]
      }
    }
  }
  dependsOn: [inputs]
}
