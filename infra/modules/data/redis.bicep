// Azure Managed Redis: one instance per environment, access only through Entra ID.

import { resourceName } from '../../shared/naming.bicep'

param envName string
param regionCode string
param location string
param tags object
param skuName string
param highAvailability bool
param logAnalyticsWorkspaceId string

@description('Identities with access to the Redis database: [{ name, principalId }].')
param accessPolicyAssignments array

// AZURE: Azure Managed Redis (resource type redisEnterprise) replaces Azure Cache for Redis. The Balanced, Memory Optimized
// and Compute Optimized tiers differ in their memory-to-vCPU ratio; B0 is the smallest instance, for dev.
resource cluster 'Microsoft.Cache/redisEnterprise@2025-04-01' = {
  name: resourceName('amr', '', envName, regionCode)
  location: location
  tags: tags
  sku: { name: skuName }
  properties: {
    minimumTlsVersion: '1.2'
    highAvailability: highAvailability ? 'Enabled' : 'Disabled'
    publicNetworkAccess: 'Disabled'
  }
}

resource database 'Microsoft.Cache/redisEnterprise/databases@2025-04-01' = {
  parent: cluster
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    port: 10000
    // WHY: the Enterprise policy: the client sees one endpoint and the server distributes the shards. Simpler client
    // configuration at the cost of one proxy hop (OSS would give direct connections to the shards).
    clusteringPolicy: 'EnterpriseCluster'
    // A cache, not a store: when memory runs out, keys with a TTL are evicted, least recently used first.
    evictionPolicy: 'VolatileLRU'
    // AZURE: access keys disabled. The application sends an Entra ID token as the AUTH password
    // (the Microsoft.Azure.StackExchangeRedis library renews it on open connections).
    accessKeysAuthentication: 'Disabled'
  }
}

// AZURE: assigning identities to the "default" access policy (full access to the database data). Isolation between
// services relies on key prefixes (rm:, catalog:, pricing:), which is a deliberate cost trade-off.
resource accessPolicies 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-04-01' = [for assignment in accessPolicyAssignments: {
  parent: database
  name: replace(assignment.name, '-', '')
  properties: {
    accessPolicyName: 'default'
    user: { objectId: assignment.principalId }
  }
}]

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: cluster
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output id string = cluster.id
output endpoint string = '${cluster.properties.hostName}:10000'
