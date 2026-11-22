// Event Hubs (Standard) with the hubs and consumer groups from messaging-topology.json, plus Capture to ADLS Gen2.

import { resourceName } from '../../shared/naming.bicep'
import { builtInRoles } from '../../shared/roles.bicep'

param envName string
param regionCode string
param location string
param tags object
param capacity int
param autoInflateMaximum int
param zoneRedundant bool
param logAnalyticsWorkspaceId string
param captureIdentityId string
param lakeStorageId string

@description('The analytics-processor identity: producer and consumer of both hubs.')
param analyticsPrincipalId string

var hubs = loadJsonContent('messaging-topology.json').eventHubs.hubs

resource namespace 'Microsoft.EventHub/namespaces@2024-01-01' = {
  name: resourceName('evhns', '', envName, regionCode)
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
    capacity: capacity
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${captureIdentityId}': {} }
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    zoneRedundant: zoneRedundant
    publicNetworkAccess: 'Disabled'
    kafkaEnabled: false
    // AZURE: auto-inflate adds throughput units during a traffic spike (a campaign), but never removes them by itself.
    // Going back to the base number is an operational task or automation on a metric.
    isAutoInflateEnabled: autoInflateMaximum > 0
    maximumThroughputUnits: autoInflateMaximum > 0 ? autoInflateMaximum : 0
  }
}

resource eventHubs 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' = [for hub in hubs: {
  parent: namespace
  name: hub.name
  properties: {
    // AZURE: the partition count cannot be changed after a hub is created in the Standard tier. It sets the upper bound of
    // consumer parallelism (one reader per partition in a group), so it is chosen with headroom.
    partitionCount: hub.partitionCount
    retentionDescription: {
      cleanupPolicy: 'Delete'
      retentionTimeInHours: hub.retentionHours
    }
    captureDescription: hub.capture
      ? {
          enabled: true
          encoding: 'Avro'
          // A file every 5 minutes or every 300 MB: fewer small files in the data lake (cheaper reads in analytics).
          intervalInSeconds: 300
          sizeLimitInBytes: 314572800
          skipEmptyArchives: true
          destination: {
            name: 'EventHubArchive.AzureBlockBlob'
            // AZURE: Capture writes with a managed identity (role on the container granted in the data layer), not with the account key.
            identity: {
              type: 'UserAssigned'
              userAssignedIdentity: captureIdentityId
            }
            properties: {
              storageAccountResourceId: lakeStorageId
              blobContainer: 'capture'
              archiveNameFormat: '{Namespace}/{EventHub}/{PartitionId}/{Year}/{Month}/{Day}/{Hour}/{Minute}/{Second}'
            }
          }
        }
      : null
  }
}]

var hubNames = map(hubs, hub => hub.name)
var consumerGroups = flatten(map(hubs, hub => map(hub.consumerGroups, group => { hub: hub.name, name: group })))

// AZURE: every consumer group has its own read position (checkpoints in the function host storage account). A new
// consumer gets a new group and reads the stream independently; it never shares a group with another consumer.
resource groups 'Microsoft.EventHub/namespaces/eventhubs/consumergroups@2024-01-01' = [for group in consumerGroups: {
  parent: eventHubs[indexOf(hubNames, group.hub)]
  name: group.name
  properties: {}
}]

var analyticsRoles = flatten(map(range(0, length(hubs)), i => [
  { hubIndex: i, role: builtInRoles.eventHubsDataSender }
  { hubIndex: i, role: builtInRoles.eventHubsDataReceiver }
]))

resource analyticsAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for entry in analyticsRoles: {
  name: guid(namespace.id, hubNames[entry.hubIndex], analyticsPrincipalId, entry.role)
  scope: eventHubs[entry.hubIndex]
  properties: {
    principalId: analyticsPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', entry.role)
  }
}]

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: namespace
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [
      { category: 'OperationalLogs', enabled: true }
      { category: 'RuntimeAuditLogs', enabled: true }
    ]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output id string = namespace.id
output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
