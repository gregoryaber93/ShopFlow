// Network: a /22 VNet with six subnets, an NSG on each, and a NAT Gateway for a fixed outbound IP.

import { resourceName } from '../../shared/naming.bicep'

param envName string
param regionCode string
param location string
param addressSpace string
param zoneRedundant bool
param tags object
param logAnalyticsWorkspaceId string

// Splitting the /22 (1024 addresses). cidrSubnet(prefix, newSize, index) computes subnets deterministically,
// so the same file works for 10.10.0.0/22 (dev) and 10.30.0.0/22 (prod).
var subnetLayout = [
  {
    name: 'snet-apim'
    prefix: cidrSubnet(addressSpace, 27, 0)
    // AZURE: APIM Standard v2 integrates with the VNet outbound through delegation to Microsoft.Web/serverFarms.
    delegation: 'Microsoft.Web/serverFarms'
    natGateway: false
  }
  {
    name: 'snet-appsvc'
    prefix: cidrSubnet(addressSpace, 27, 1)
    delegation: 'Microsoft.Web/serverFarms'
    natGateway: true
  }
  {
    name: 'snet-runners'
    prefix: cidrSubnet(addressSpace, 26, 1)
    // AZURE: GitHub runners with Azure private networking require a subnet delegated to GitHub.Network/networkSettings.
    // Every concurrent job takes an address; /26 is about 59 runners at once (Azure reserves 5 addresses in a subnet).
    delegation: 'GitHub.Network/networkSettings'
    natGateway: true
  }
  {
    name: 'snet-func'
    prefix: cidrSubnet(addressSpace, 24, 1)
    // AZURE: Flex Consumption requires delegation to Microsoft.App/environments (not Microsoft.Web like other plans).
    // A /24 subnet, because every instance takes an address when scaling.
    delegation: 'Microsoft.App/environments'
    natGateway: true
  }
  {
    name: 'snet-aca'
    prefix: cidrSubnet(addressSpace, 24, 2)
    // AZURE: a Container Apps environment with workload profiles requires delegation and at least a /27; /24 leaves room for replicas.
    delegation: 'Microsoft.App/environments'
    natGateway: true
  }
  {
    name: 'snet-pe'
    prefix: cidrSubnet(addressSpace, 24, 3)
    delegation: ''
    natGateway: false
  }
]

resource natPublicIp 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: resourceName('pip', 'nat', envName, regionCode)
  location: location
  tags: tags
  sku: { name: 'Standard' }
  zones: zoneRedundant ? ['1', '2', '3'] : []
  properties: {
    publicIPAllocationMethod: 'Static'
  }
}

// AZURE: the NAT Gateway gives functions, App Service and Container Apps a fixed outbound address. The payment provider and
// the carrier put it on their IP allowlists. Without a NAT Gateway traffic leaves from a pool of platform addresses that
// changes without warning, and under heavy load SNAT ports run out.
resource natGateway 'Microsoft.Network/natGateways@2024-05-01' = {
  name: resourceName('ng', 'egress', envName, regionCode)
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: {
    idleTimeoutInMinutes: 10
    publicIpAddresses: [{ id: natPublicIp.id }]
  }
}

resource nsgs 'Microsoft.Network/networkSecurityGroups@2024-05-01' = [for subnet in subnetLayout: {
  name: resourceName('nsg', replace(subnet.name, 'snet-', ''), envName, regionCode)
  location: location
  tags: tags
  properties: {
    // WHY: the default NSG rules allow traffic inside the VNet, which is enough (all services talk through private endpoints).
    // The explicit rule only blocks inbound internet traffic to the private endpoint subnet.
    securityRules: subnet.name == 'snet-pe'
      ? [
          {
            name: 'deny-internet-inbound'
            properties: {
              priority: 4000
              direction: 'Inbound'
              access: 'Deny'
              protocol: '*'
              sourceAddressPrefix: 'Internet'
              sourcePortRange: '*'
              destinationAddressPrefix: '*'
              destinationPortRange: '*'
            }
          }
        ]
      : []
  }
}]

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: resourceName('vnet', '', envName, regionCode)
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: [addressSpace] }
    // WHY: subnets as a VNet property, not as separate resources. Separate subnet resources would be removed from the VNet
    // definition and recreated on a VNet redeployment (the "InUseSubnetCannotBeDeleted" conflict).
    subnets: [for (subnet, i) in subnetLayout: {
      name: subnet.name
      properties: {
        addressPrefix: subnet.prefix
        networkSecurityGroup: { id: nsgs[i].id }
        natGateway: subnet.natGateway ? { id: natGateway.id } : null
        delegations: empty(subnet.delegation) ? [] : [
          {
            name: 'delegation'
            properties: { serviceName: subnet.delegation }
          }
        ]
        privateEndpointNetworkPolicies: subnet.name == 'snet-pe' ? 'Enabled' : 'Disabled'
        defaultOutboundAccess: false
      }
    }]
  }
}

resource vnetDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: vnet
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output vnetId string = vnet.id
output subnetIds object = toObject(subnetLayout, subnet => subnet.name, subnet => '${vnet.id}/subnets/${subnet.name}')
output natPublicIpAddress string = natPublicIp.properties.ipAddress
