// ShopFlow: infrastructure entry point for one environment.
//   az deployment sub what-if  --location westeurope --template-file infra/main.bicep --parameters infra/parameters/dev.bicepparam
//   az deployment sub create   --location westeurope --template-file infra/main.bicep --parameters infra/parameters/dev.bicepparam
// Pipeline: what-if in a pull request (.github/workflows/infra-validate.yml), deployment in release.yml (.github/actions/bicep-deploy).
// Resource groups, pipeline identities, their permissions and the budget are created beforehand by an administrator: infra/bootstrap/.

targetScope = 'subscription'

import { environmentConfig } from 'shared/types.bicep'

param config environmentConfig

@description('Image tag for an application that is not in imageTags yet (first deployment): the commit SHA.')
param imageTag string

@description('Image tags of the running container applications, e.g. {"inventory-api": "3f2a…"}. Computed by the pipeline.')
param imageTags object = {}

@description('Container registry shared by all environments.')
param acrName string = 'crshopflowsharedweu'
param acrResourceGroup string = 'rg-shopflow-shared-weu'

@description('Second pass after the Private Link connection from Front Door is approved: APIM without a public endpoint (see the apim module).')
param apimPrivateOnly bool = false

// WHY: three groups by lifecycle. The network and identities change rarely and have separate permissions,
// data has a delete lock, and applications are deployed many times a day.
// The groups are created by the bootstrap (infra/bootstrap/environment.bicep), because the pipeline permissions are granted
// in their scope: an identity with the Contributor role on three groups cannot create a group or change anything outside them.
resource coreGroup 'Microsoft.Resources/resourceGroups@2024-03-01' existing = {
  name: 'rg-shopflow-${config.name}-${config.regionCode}-core'
}

resource dataGroup 'Microsoft.Resources/resourceGroups@2024-03-01' existing = {
  name: 'rg-shopflow-${config.name}-${config.regionCode}-data'
}

resource appsGroup 'Microsoft.Resources/resourceGroups@2024-03-01' existing = {
  name: 'rg-shopflow-${config.name}-${config.regionCode}-apps'
}

module core 'layers/core.bicep' = {
  name: 'core-${config.name}'
  scope: coreGroup
  params: { config: config }
}

module data 'layers/data.bicep' = {
  name: 'data-${config.name}'
  scope: dataGroup
  params: {
    config: config
    subnetIds: core.outputs.subnetIds
    privateDnsZoneIds: core.outputs.privateDnsZoneIds
    logAnalyticsWorkspaceId: core.outputs.logAnalyticsWorkspaceId
    identities: core.outputs.identities
    platformIdentities: core.outputs.platformIdentities
  }
}

module messaging 'layers/messaging.bicep' = {
  name: 'messaging-${config.name}'
  scope: dataGroup
  params: {
    config: config
    subnetIds: core.outputs.subnetIds
    privateDnsZoneIds: core.outputs.privateDnsZoneIds
    logAnalyticsWorkspaceId: core.outputs.logAnalyticsWorkspaceId
    identities: core.outputs.identities
    platformIdentities: core.outputs.platformIdentities
    docsStorageId: data.outputs.docsStorageId
    lakeStorageId: data.outputs.lakeStorageId
  }
}

module apps 'layers/apps.bicep' = {
  name: 'apps-${config.name}'
  scope: appsGroup
  params: {
    config: config
    subnetIds: core.outputs.subnetIds
    privateDnsZoneIds: core.outputs.privateDnsZoneIds
    vnetId: core.outputs.vnetId
    natPublicIpAddress: core.outputs.natPublicIpAddress
    logAnalyticsWorkspaceId: core.outputs.logAnalyticsWorkspaceId
    appInsightsConnectionString: core.outputs.appInsightsConnectionString
    identities: core.outputs.identities
    acrName: acrName
    acrResourceGroup: acrResourceGroup
    imageTag: imageTag
    imageTags: imageTags
    cosmosEndpoint: data.outputs.cosmosEndpoint
    sqlServerFqdn: data.outputs.sqlServerFqdn
    sqlDatabaseName: data.outputs.sqlDatabaseName
    redisEndpoint: data.outputs.redisEndpoint
    docsBlobEndpoint: data.outputs.docsBlobEndpoint
    functionsStorageName: data.outputs.functionsStorageName
    functionsBlobEndpoint: data.outputs.functionsBlobEndpoint
    appConfigurationEndpoint: data.outputs.appConfigurationEndpoint
    serviceBusNamespace: messaging.outputs.serviceBusNamespace
    eventHubsNamespace: messaging.outputs.eventHubsNamespace
    communicationEndpoint: data.outputs.communicationEndpoint
    emailSenderAddress: data.outputs.emailSenderAddress
  }
}

module edge 'layers/edge.bicep' = {
  name: 'edge-${config.name}'
  scope: appsGroup
  params: {
    config: config
    subnetIds: core.outputs.subnetIds
    logAnalyticsWorkspaceId: core.outputs.logAnalyticsWorkspaceId
    platformIdentities: core.outputs.platformIdentities
    keyVaultUri: data.outputs.keyVaultUri
    docsStorageId: data.outputs.docsStorageId
    backends: apps.outputs.backends
    apimPublicNetworkAccess: apimPrivateOnly ? 'Disabled' : 'Enabled'
  }
}

module operations 'layers/operations.bicep' = {
  name: 'operations-${config.name}'
  scope: coreGroup
  params: {
    config: config
    logAnalyticsWorkspaceId: core.outputs.logAnalyticsWorkspaceId
    appInsightsId: core.outputs.appInsightsId
    serviceBusId: messaging.outputs.serviceBusId
    cosmosAccountId: data.outputs.cosmosAccountId
  }
}

output apiHostName string = edge.outputs.apiHostName
output filesHostName string = edge.outputs.filesHostName
output natPublicIpAddress string = core.outputs.natPublicIpAddress

@description('Value of the GitHubId tag of the networkSettings resource: it is entered in the runner network configuration in GitHub.')
output githubRunnersNetworkId string = core.outputs.githubRunnersNetworkId
