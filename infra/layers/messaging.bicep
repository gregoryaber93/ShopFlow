// Messaging layer (in the -data group): Service Bus, Event Hubs, Event Grid and their private endpoints.

import { environmentConfig } from '../shared/types.bicep'
import { tags } from '../shared/naming.bicep'
import { deployableUnits } from '../shared/roles.bicep'

param config environmentConfig
param subnetIds object
param privateDnsZoneIds object
param logAnalyticsWorkspaceId string
param identities object
param platformIdentities object
param docsStorageId string
param lakeStorageId string

var messagingTags = tags(config.name, 'platform-messaging', 'confidential')

module serviceBus '../modules/messaging/servicebus.bicep' = {
  name: 'service-bus'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: config.location
    tags: messagingTags
    capacity: config.serviceBus.capacity
    zoneRedundant: config.zoneRedundant
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    principalIds: toObject(deployableUnits, unit => unit, unit => identities[unit].principalId)
    scalerQueueReaders: [{ queue: 'inventory-commands', unit: 'inventory-worker' }]
    eventGridPrincipalId: platformIdentities['eventgrid-docs'].principalId
  }
}

module eventHubs '../modules/messaging/eventhubs.bicep' = {
  name: 'event-hubs'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: config.location
    tags: messagingTags
    capacity: config.eventHubs.capacity
    autoInflateMaximum: config.eventHubs.autoInflateMaximum
    zoneRedundant: config.zoneRedundant
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    captureIdentityId: platformIdentities['evh-capture'].id
    lakeStorageId: lakeStorageId
    analyticsPrincipalId: identities['analytics-processor'].principalId
  }
}

module eventGrid '../modules/messaging/eventgrid-invoices.bicep' = {
  name: 'event-grid-invoices'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: config.location
    tags: messagingTags
    docsStorageId: docsStorageId
    invoiceQueueId: serviceBus.outputs.invoiceBlobEventsQueueId
    identityId: platformIdentities['eventgrid-docs'].id
  }
}

// Service Bus and Event Hubs use the same privatelink.servicebus.windows.net zone and the "namespace" sub-resource.
module serviceBusEndpoint '../modules/network/private-endpoint.bicep' = {
  name: 'pe-servicebus'
  params: {
    name: 'pe-shopflow-sbns-${config.name}-${config.regionCode}'
    location: config.location
    tags: messagingTags
    subnetId: subnetIds['snet-pe']
    targetResourceId: serviceBus.outputs.id
    groupId: 'namespace'
    privateDnsZoneId: privateDnsZoneIds['privatelink.servicebus.windows.net']
  }
}

module eventHubsEndpoint '../modules/network/private-endpoint.bicep' = {
  name: 'pe-eventhubs'
  params: {
    name: 'pe-shopflow-evhns-${config.name}-${config.regionCode}'
    location: config.location
    tags: messagingTags
    subnetId: subnetIds['snet-pe']
    targetResourceId: eventHubs.outputs.id
    groupId: 'namespace'
    privateDnsZoneId: privateDnsZoneIds['privatelink.servicebus.windows.net']
  }
}

output serviceBusNamespace string = serviceBus.outputs.fullyQualifiedNamespace
output serviceBusId string = serviceBus.outputs.id
output eventHubsNamespace string = eventHubs.outputs.fullyQualifiedNamespace
