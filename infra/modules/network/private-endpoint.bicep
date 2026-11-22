// Private endpoint with registration in a private DNS zone.

param name string
param location string
param tags object
param subnetId string
param targetResourceId string

@description('Service sub-resource, e.g. sqlServer, namespace, blob, Sql (Cosmos), vault, configurationStores, sites.')
param groupId string

param privateDnsZoneId string

resource endpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    subnet: { id: subnetId }
    customNetworkInterfaceName: '${name}-nic'
    privateLinkServiceConnections: [
      {
        name: name
        properties: {
          privateLinkServiceId: targetResourceId
          groupIds: [groupId]
        }
      }
    ]
  }
}

// AZURE: the DNS zone group creates and deletes the A record in the private zone together with the endpoint lifecycle.
// Manual A records remain after the endpoint is deleted and send traffic nowhere.
resource dnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: endpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: replace(last(split(privateDnsZoneId, '/')), '.', '-')
        properties: { privateDnsZoneId: privateDnsZoneId }
      }
    ]
  }
}

output id string = endpoint.id
