// Private DNS zones for private endpoints, linked to the VNet.
// AZURE: a private endpoint gives a service a private address, but the application connects by the usual name
// (e.g. cosmos-shopflow-prod-weu.documents.azure.com). Public DNS returns a CNAME to *.privatelink.documents.azure.com
// for it, and this private zone, linked to the VNet, resolves that to the private address.
// Without the zone an application in the VNet would get the public address, which is blocked (publicNetworkAccess: Disabled).

param vnetId string
param tags object

@description('Zones shared by all services with a private endpoint.')
var zoneNames = [
  'privatelink.documents.azure.com'
  'privatelink.servicebus.windows.net' // Service Bus i Event Hubs
  'privatelink.database.windows.net'
  'privatelink.redis.azure.net' // Azure Managed Redis
  'privatelink.blob.${environment().suffixes.storage}'
  'privatelink.queue.${environment().suffixes.storage}'
  'privatelink.table.${environment().suffixes.storage}'
  'privatelink.dfs.${environment().suffixes.storage}'
  'privatelink.vaultcore.azure.net'
  'privatelink.azconfig.io'
  'privatelink.azurewebsites.net' // App Service i function apps
  'privatelink.azure-api.net' // API Management inbound
]

resource zones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zone in zoneNames: {
  name: zone
  location: 'global'
  tags: tags
}]

resource links 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zone, i) in zoneNames: {
  parent: zones[i]
  name: 'link-core'
  location: 'global'
  tags: tags
  properties: {
    virtualNetwork: { id: vnetId }
    registrationEnabled: false
  }
}]

// WHY: resourceId() instead of zones[i].id in a lambda. Bicep does not allow indexing a resource collection with a lambda
// variable, and a zone identifier is deterministic (group + type + name). The dependency on the zones being created is
// ensured by consumers referencing this module's output.
@description('Map: zone name → resource identifier.')
output zoneIds object = toObject(zoneNames, zone => zone, zone => resourceId('Microsoft.Network/privateDnsZones', zone))
