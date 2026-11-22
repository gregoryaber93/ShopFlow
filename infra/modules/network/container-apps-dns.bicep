// Private DNS zone for the default domain of the internal Container Apps environment.
// WHY: a separate module, because the zone name is the environment's domain, known only after the environment is created.
// A resource name cannot be computed from another resource's property in the same template, but it can be a module parameter.

param defaultDomain string
param staticIp string
param vnetId string
param tags object

resource zone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: defaultDomain
  location: 'global'
  tags: tags
}

resource link 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: zone
  name: 'link-core'
  location: 'global'
  tags: tags
  properties: {
    virtualNetwork: { id: vnetId }
    registrationEnabled: false
  }
}

// Every application is <name>.<domain>, so one wildcard record points all of them at the environment address.
resource wildcard 'Microsoft.Network/privateDnsZones/A@2020-06-01' = {
  parent: zone
  name: '*'
  properties: {
    ttl: 300
    aRecords: [{ ipv4Address: staticIp }]
  }
}
