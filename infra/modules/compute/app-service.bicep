// App Service for orders-api: Linux plan, staging slot, autoscale, inbound private endpoint.

import { resourceName } from '../../shared/naming.bicep'

param envName string
param regionCode string
param location string
param tags object
param skuName string
param minInstances int
param maxInstances int
param zoneRedundant bool
param identityId string
param integrationSubnetId string
param logAnalyticsWorkspaceId string

@description('Application settings (name → value). Shared by the production and staging slots.')
param appSettings object

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: resourceName('asp', 'orders', envName, regionCode)
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: skuName
    capacity: minInstances
  }
  properties: {
    reserved: true
    // AZURE: zone redundancy of the plan requires at least three instances (one per zone).
    zoneRedundant: zoneRedundant
  }
}

var productionSettings = [for setting in items(union(appSettings, { SLOT_NAME: 'production' })): { name: setting.key, value: setting.value }]
var stagingSettings = [for setting in items(union(appSettings, { SLOT_NAME: 'staging' })): { name: setting.key, value: setting.value }]

var siteConfig = {
  linuxFxVersion: 'DOTNETCORE|10.0'
  alwaysOn: true
  http20Enabled: true
  minTlsVersion: '1.2'
  ftpsState: 'Disabled'
  // AZURE: the health check removes from the load balancer an instance that answers /health/ready with an error for a few
  // minutes, and restarts it after a longer time. It is also checked during a slot swap.
  healthCheckPath: '/health/ready'
  // All outbound traffic goes through the VNet (to the Cosmos, Redis and inventory-api private endpoints) and the NAT Gateway.
  vnetRouteAllEnabled: true
  appSettings: productionSettings
}

resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: resourceName('app', 'orders', envName, regionCode)
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    // AZURE: traffic comes in only through the private endpoint (from APIM in the VNet). The public *.azurewebsites.net address does not answer.
    publicNetworkAccess: 'Disabled'
    virtualNetworkSubnetId: integrationSubnetId
    keyVaultReferenceIdentity: identityId
    clientAffinityEnabled: false
    siteConfig: siteConfig
  }
}

// AZURE: the staging slot is a second, fully working copy of the application on the same plan. The pipeline deploys to staging,
// warms it up and runs a smoke test, then swaps the slots. The swap switches traffic without a cold start,
// and a rollback is another swap: the old version is still running in the staging slot.
resource staging 'Microsoft.Web/sites/slots@2023-12-01' = {
  parent: site
  name: 'staging'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Disabled'
    virtualNetworkSubnetId: integrationSubnetId
    keyVaultReferenceIdentity: identityId
    siteConfig: union(siteConfig, {
      // Staging gets no production traffic, so it may go to sleep.
      alwaysOn: false
      appSettings: stagingSettings
    })
  }
}

// Slot-sticky settings do not travel with a swap. SLOT_NAME lets the application and its logs tell the slots apart.
resource stickySettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: site
  name: 'slotConfigNames'
  properties: {
    appSettingNames: ['SLOT_NAME']
  }
}

resource autoscale 'Microsoft.Insights/autoscalesettings@2022-10-01' = {
  name: resourceName('as', 'orders', envName, regionCode)
  location: location
  tags: tags
  properties: {
    enabled: true
    targetResourceUri: plan.id
    profiles: [
      {
        name: 'cpu'
        capacity: {
          minimum: string(minInstances)
          maximum: string(maxInstances)
          default: string(minInstances)
        }
        rules: [
          {
            metricTrigger: {
              metricName: 'CpuPercentage'
              metricResourceUri: plan.id
              timeGrain: 'PT1M'
              statistic: 'Average'
              timeWindow: 'PT5M'
              timeAggregation: 'Average'
              operator: 'GreaterThan'
              threshold: 70
            }
            scaleAction: { direction: 'Increase', type: 'ChangeCount', value: '2', cooldown: 'PT5M' }
          }
          {
            // WHY: asymmetry. Scale out quickly and by two instances, scale in slowly and by one. Overly aggressive scale-in with
            // fluctuating traffic ends in "flapping" and cold instances.
            metricTrigger: {
              metricName: 'CpuPercentage'
              metricResourceUri: plan.id
              timeGrain: 'PT1M'
              statistic: 'Average'
              timeWindow: 'PT15M'
              timeAggregation: 'Average'
              operator: 'LessThan'
              threshold: 30
            }
            scaleAction: { direction: 'Decrease', type: 'ChangeCount', value: '1', cooldown: 'PT15M' }
          }
        ]
      }
    ]
  }
}

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: site
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      { category: 'AppServiceHTTPLogs', enabled: true }
      { category: 'AppServiceConsoleLogs', enabled: true }
      { category: 'AppServicePlatformLogs', enabled: true }
    ]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output id string = site.id
output defaultHostName string = site.properties.defaultHostName
