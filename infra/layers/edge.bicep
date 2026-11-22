// Edge layer (in the -apps group): API Management and Front Door with WAF.

import { environmentConfig } from '../shared/types.bicep'
import { tags } from '../shared/naming.bicep'

param config environmentConfig
param subnetIds object
param logAnalyticsWorkspaceId string
param platformIdentities object
param keyVaultUri string
param docsStorageId string
param backends object

@allowed(['Enabled', 'Disabled'])
param apimPublicNetworkAccess string = 'Enabled'

var edgeTags = tags(config.name, 'platform-edge', 'public')

module apim '../modules/edge/apim.bicep' = {
  name: 'api-management'
  params: {
    config: config
    tags: edgeTags
    subnetId: subnetIds['snet-apim']
    identityId: platformIdentities.apim.id
    identityClientId: platformIdentities.apim.clientId
    keyVaultUri: keyVaultUri
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    backends: backends
    publicNetworkAccess: apimPublicNetworkAccess
  }
}

module frontDoor '../modules/edge/front-door.bicep' = {
  name: 'front-door'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: config.location
    tags: edgeTags
    publicDomain: config.publicDomain
    apimId: apim.outputs.id
    apimGatewayHostName: apim.outputs.gatewayHostName
    docsStorageId: docsStorageId
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
  }
}

module gatewayPolicy '../modules/edge/apim-gateway-policy.bicep' = {
  name: 'api-management-global-policy'
  params: {
    apimName: apim.outputs.name
    frontDoorId: frontDoor.outputs.frontDoorId
  }
}

output apiHostName string = frontDoor.outputs.apiHostName
output filesHostName string = frontDoor.outputs.filesHostName
