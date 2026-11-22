// Event Grid: the BlobCreated event for a new invoice goes to the invoice-blob-events Service Bus queue.
// Blob Storage with Event Grid acts as an outbox for files here (writing a file and publishing cannot be covered by a transaction).

import { resourceName } from '../../shared/naming.bicep'

param envName string
param regionCode string
param location string
param tags object
param docsStorageId string
param invoiceQueueId string
param identityId string

resource systemTopic 'Microsoft.EventGrid/systemTopics@2022-06-15' = {
  name: resourceName('evgt', 'docs', envName, regionCode)
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    source: docsStorageId
    topicType: 'Microsoft.Storage.StorageAccounts'
  }
}

resource invoiceUploaded 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2022-06-15' = {
  parent: systemTopic
  name: 'invoice-uploaded'
  properties: {
    // CloudEvents 1.0: the standard format that InvoiceBlobEventsFunction parses as a CloudEvent.
    eventDeliverySchema: 'CloudEventSchemaV1_0'
    filter: {
      includedEventTypes: ['Microsoft.Storage.BlobCreated']
      subjectBeginsWith: '/blobServices/default/containers/invoices/'
      subjectEndsWith: '.pdf'
    }
    // AZURE: delivery with a managed identity (Data Sender role on this one queue), because the Service Bus namespace has no
    // SAS keys. Event Grid connects as a trusted service (networkRuleSets in the Service Bus module).
    deliveryWithResourceIdentity: {
      identity: {
        type: 'UserAssigned'
        userAssignedIdentity: identityId
      }
      destination: {
        endpointType: 'ServiceBusQueue'
        properties: { resourceId: invoiceQueueId }
      }
    }
    retryPolicy: {
      maxDeliveryAttempts: 30
      eventTimeToLiveInMinutes: 1440
    }
    // An event that could not be delivered for 24 h is not lost: it goes to the eventgrid-deadletter container.
    deadLetterWithResourceIdentity: {
      identity: {
        type: 'UserAssigned'
        userAssignedIdentity: identityId
      }
      deadLetterDestination: {
        endpointType: 'StorageBlob'
        properties: {
          resourceId: docsStorageId
          blobContainerName: 'eventgrid-deadletter'
        }
      }
    }
  }
}

output systemTopicId string = systemTopic.id
