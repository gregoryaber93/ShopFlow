// User-assigned managed identities: one per deployable unit, plus platform identities.

import { resourceName } from '../../shared/naming.bicep'
import { builtInRoles, deployableUnits } from '../../shared/roles.bicep'

param envName string
param regionCode string
param location string
param tags object
param appInsightsId string

// WHY: user-assigned, not system-assigned. The identity exists before the application, so roles in Cosmos, Service Bus or
// SQL can be granted before the compute exists (no "application starts without permissions" race). The staging and
// production App Service slots share the same identity, and deleting and recreating the application does not lose permissions.
var platformIdentities = [
  // Event Hubs Capture writes to ADLS Gen2 with this identity (the role on the lake account is granted before the hub is created).
  'evh-capture'
  // The Event Grid system topic delivers BlobCreated events to the Service Bus queue with this identity.
  'eventgrid-docs'
  // API Management: named values from Key Vault and the token for backends (authentication-managed-identity policy).
  'apim'
]

resource unitIdentities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [for unit in deployableUnits: {
  name: resourceName('id', unit, envName, regionCode)
  location: location
  tags: tags
}]

resource platform 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [for name in platformIdentities: {
  name: resourceName('id', name, envName, regionCode)
  location: location
  tags: tags
}]

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: last(split(appInsightsId, '/'))
}

// Every application sends telemetry with an Entra ID token (Application Insights has local auth disabled).
resource telemetryPublishers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (unit, i) in deployableUnits: {
  name: guid(appInsights.id, unitIdentities[i].id, builtInRoles.monitoringMetricsPublisher)
  scope: appInsights
  properties: {
    principalId: unitIdentities[i].properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', builtInRoles.monitoringMetricsPublisher)
  }
}]

// Lists from a for loop (not from a lambda: Bicep does not allow indexing a resource collection with a lambda variable).
// The core layer turns them into name → identity maps.
@description('[{ name, id, principalId, clientId }] for the deployable units.')
output units array = [for (unit, i) in deployableUnits: {
  name: unit
  id: unitIdentities[i].id
  principalId: unitIdentities[i].properties.principalId
  clientId: unitIdentities[i].properties.clientId
}]

output platform array = [for (name, i) in platformIdentities: {
  name: name
  id: platform[i].id
  principalId: platform[i].properties.principalId
  clientId: platform[i].properties.clientId
}]

