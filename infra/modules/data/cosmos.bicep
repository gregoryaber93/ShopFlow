// Cosmos DB (NoSQL API): one account per environment, a separate database per bounded context, data-plane RBAC per database.

import { resourceName } from '../../shared/naming.bicep'
import { cosmosDataRoles } from '../../shared/roles.bicep'

param envName string
param regionCode string
param location string
param tags object
param serverless bool
param maxAutoscaleThroughput int
param zoneRedundant bool
param logAnalyticsWorkspaceId string

@description('Databases and containers: [{ name, containers: [{ name, partitionKey, defaultTtl? }] }].')
param databases array

@description('Data-plane assignments: [{ principalId, database, role: dataReader|dataContributor }].')
param dataRoleAssignments array

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: resourceName('cosmos', '', envName, regionCode)
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: zoneRedundant
      }
    ]
    // WHY: Session is the default and cheapest consistency level that gives "read your own writes" within an SDK client
    // session. Strong adds nothing in one region and doubles write latency with several regions.
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    capabilities: serverless ? [{ name: 'EnableServerless' }] : []
    // AZURE: account keys disabled; access only with an Entra ID token and data-plane roles (sqlRoleAssignments).
    disableLocalAuth: true
    // AZURE: blocks creating databases and containers from the SDK even with a token that has permissions. The structure is
    // created only here, so a typo in a container name in code ends with an error, not a new container.
    disableKeyBasedMetadataWriteAccess: true
    publicNetworkAccess: 'Disabled'
    minimalTlsVersion: 'Tls12'
    // AZURE: continuous backup (point-in-time restore) instead of periodic. Restore to any second of the last 7 days without
    // a support ticket; after accidentally deleting a container recovery takes minutes, not days.
    backupPolicy: {
      type: 'Continuous'
      continuousModeProperties: { tier: envName == 'prod' ? 'Continuous30Days' : 'Continuous7Days' }
    }
    enableAutomaticFailover: false
  }
}

resource sqlDatabases 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = [for database in databases: {
  parent: account
  name: database.name
  properties: {
    resource: { id: database.name }
    // WHY: throughput at the database level (shared by the service's containers) instead of per container. A leases or
    // idempotency container uses a fraction of the RU and should not have its own 1000 RU/s minimum.
    options: serverless ? {} : { autoscaleSettings: { maxThroughput: maxAutoscaleThroughput } }
  }
}]

var databaseNames = map(databases, database => database.name)
var containers = flatten(map(databases, database => map(database.containers, container => union(container, { database: database.name }))))

resource sqlContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = [for container in containers: {
  parent: sqlDatabases[indexOf(databaseNames, container.database)]
  name: container.name
  properties: {
    resource: union(
      {
        id: container.name
        partitionKey: { paths: [container.partitionKey], kind: 'Hash', version: 2 }
        indexingPolicy: {
          indexingMode: 'consistent'
          automatic: true
          includedPaths: [{ path: '/*' }]
          // WHY: the outbox and inbox payloads are never filtered by a query, and indexing large fields costs RU on every write.
          // Excluding them from the index lowers the cost of writing an order.
          excludedPaths: [{ path: '/payload/*' }, { path: '/"_etag"/?' }]
        }
      },
      // AZURE: defaultTtl = -1 enables TTL without a default expiry: a document lives forever unless it has its own ttl field
      // (outbox and inbox records do). Without the property TTL is disabled entirely and the ttl field is ignored.
      contains(container, 'defaultTtl') ? { defaultTtl: container.defaultTtl } : {})
  }
}]

// AZURE: data-plane roles are a separate Cosmos DB RBAC system (sqlRoleAssignments), not Azure RBAC. The scope is the
// /dbs/{database} path, so the Orders identity cannot read the payments database even though the account is shared.
resource dataRoles 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = [for assignment in dataRoleAssignments: {
  parent: account
  name: guid(account.id, assignment.principalId, assignment.database, assignment.role)
  properties: {
    principalId: assignment.principalId
    roleDefinitionId: '${account.id}/sqlRoleDefinitions/${cosmosDataRoles[assignment.role]}'
    scope: '${account.id}/dbs/${assignment.database}'
  }
  dependsOn: [sqlDatabases]
}]

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: account
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    // Resource-specific tables (CDBDataPlaneRequests etc.) instead of AzureDiagnostics: cheaper and with typed columns
    // (queries about 429s and RU per container in docs/kql).
    logAnalyticsDestinationType: 'Dedicated'
    logs: [
      { category: 'DataPlaneRequests', enabled: true }
      { category: 'PartitionKeyRUConsumption', enabled: true }
      { category: 'ControlPlaneRequests', enabled: true }
    ]
    metrics: [{ category: 'Requests', enabled: true }]
  }
}

output id string = account.id
output name string = account.name
output endpoint string = account.properties.documentEndpoint
