// Core layer (rg-shopflow-{env}-weu-core): network, DNS, monitoring, identities.
// The slowest-changing part; the other layers get identifiers from here through outputs.

import { environmentConfig } from '../shared/types.bicep'
import { tags } from '../shared/naming.bicep'

param config environmentConfig

var coreTags = tags(config.name, 'platform-core', 'internal')

module observability '../modules/monitoring/observability.bicep' = {
  name: 'observability'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: config.location
    tags: coreTags
    retentionDays: config.monitoring.retentionDays
    dailyQuotaGb: config.monitoring.dailyQuotaGb
    samplingPercentage: config.monitoring.samplingPercentage
  }
}

module network '../modules/network/vnet.bicep' = {
  name: 'network'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: config.location
    addressSpace: config.addressSpace
    zoneRedundant: config.zoneRedundant
    tags: coreTags
    logAnalyticsWorkspaceId: observability.outputs.workspaceId
  }
}

module dnsZones '../modules/network/private-dns-zones.bicep' = {
  name: 'private-dns-zones'
  params: {
    vnetId: network.outputs.vnetId
    tags: coreTags
  }
}

// AZURE: GitHub runners (hosted by GitHub) get network interfaces in our subnet. Code deployment to App Service and
// Functions goes through the SCM endpoint, which has only a private address, so the runner must be in the VNet.
module githubRunners '../modules/network/github-runners.bicep' = {
  name: 'github-runners'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: config.location
    tags: coreTags
    subnetId: network.outputs.subnetIds['snet-runners']
    businessId: config.githubBusinessId
  }
}

module identities '../modules/identity/identities.bicep' = {
  name: 'identities'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: config.location
    tags: coreTags
    appInsightsId: observability.outputs.appInsightsId
  }
}

output subnetIds object = network.outputs.subnetIds
output vnetId string = network.outputs.vnetId
output natPublicIpAddress string = network.outputs.natPublicIpAddress
output privateDnsZoneIds object = dnsZones.outputs.zoneIds
output logAnalyticsWorkspaceId string = observability.outputs.workspaceId
output appInsightsId string = observability.outputs.appInsightsId
output appInsightsConnectionString string = observability.outputs.appInsightsConnectionString
@description('Map: unit → { id, principalId, clientId }.')
output identities object = toObject(identities.outputs.units, identity => identity.name, identity => {
  id: identity.id
  principalId: identity.principalId
  clientId: identity.clientId
})

output platformIdentities object = toObject(identities.outputs.platform, identity => identity.name, identity => {
  id: identity.id
  principalId: identity.principalId
  clientId: identity.clientId
})
output githubRunnersNetworkId string = githubRunners.outputs.githubId
