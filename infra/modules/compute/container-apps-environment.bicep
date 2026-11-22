// Container Apps environment: internal (no public IP), workload profiles, logs through Azure Monitor.

import { resourceName } from '../../shared/naming.bicep'

param envName string
param regionCode string
param location string
param tags object
param infrastructureSubnetId string
param zoneRedundant bool
param logAnalyticsWorkspaceId string

resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: resourceName('cae', '', envName, regionCode)
  location: location
  tags: tags
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: infrastructureSubnetId
      // AZURE: an internal environment gets a static private address in the VNet instead of a public one. Applications
      // with "external" ingress are reachable from the whole VNet (APIM, orders-api), but not from the internet.
      internal: true
    }
    workloadProfiles: [
      { name: 'Consumption', workloadProfileType: 'Consumption' }
    ]
    zoneRedundant: zoneRedundant
    // AZURE: "azure-monitor" instead of "log-analytics": logs go through diagnostic settings, so the environment does not
    // need the workspace shared key (which would be a secret in configuration).
    appLogsConfiguration: { destination: 'azure-monitor' }
  }
}

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: managedEnvironment
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [
      { category: 'ContainerAppConsoleLogs', enabled: true }
      { category: 'ContainerAppSystemLogs', enabled: true }
    ]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output id string = managedEnvironment.id
output defaultDomain string = managedEnvironment.properties.defaultDomain
output staticIp string = managedEnvironment.properties.staticIp
