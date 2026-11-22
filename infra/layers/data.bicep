// Data layer (rg-shopflow-{env}-weu-data): databases, cache, storage, configuration, secrets, e-mail.
// Data permissions are granted here, next to the resource, not next to the application: this file shows who can read
// and write what.

import { environmentConfig } from '../shared/types.bicep'
import { compactName, tags } from '../shared/naming.bicep'
import { builtInRoles, deployableUnits } from '../shared/roles.bicep'

param config environmentConfig
param subnetIds object
param privateDnsZoneIds object
param logAnalyticsWorkspaceId string
param identities object
param platformIdentities object

var dataTags = tags(config.name, 'platform-data', 'confidential')
var location = config.location
var storageSuffix = environment().suffixes.storage

// --- Cosmos DB ----------------------------------------------------------------------------------------------

var cosmosDatabases = [
  {
    name: 'orders'
    containers: [
      { name: 'orders', partitionKey: '/orderId', defaultTtl: -1 }
      { name: 'carts', partitionKey: '/customerId', defaultTtl: 2592000 } // 30 days since the last change
      { name: 'idempotency', partitionKey: '/scopeKey', defaultTtl: 86400 } // 24 h
      { name: 'leases', partitionKey: '/id' }
    ]
  }
  { name: 'saga', containers: [{ name: 'saga-state', partitionKey: '/orderId', defaultTtl: 7776000 }] } // 90 days
  {
    name: 'payments'
    containers: [
      { name: 'payments', partitionKey: '/orderId', defaultTtl: -1 }
      { name: 'leases', partitionKey: '/id' }
    ]
  }
  {
    name: 'shipping'
    containers: [
      { name: 'shipments', partitionKey: '/orderId', defaultTtl: -1 }
      { name: 'leases', partitionKey: '/id' }
    ]
  }
  { name: 'notifications', containers: [{ name: 'notification-log', partitionKey: '/customerId', defaultTtl: -1 }] }
  { name: 'readmodels', containers: [{ name: 'customer-orders', partitionKey: '/customerId' }] }
  { name: 'analytics', containers: [{ name: 'aggregates', partitionKey: '/metricId', defaultTtl: -1 }] }
]

var cosmosAccess = [
  { unit: 'orders-api', database: 'orders', role: 'dataContributor' }
  { unit: 'orders-processors', database: 'orders', role: 'dataContributor' }
  { unit: 'order-saga', database: 'saga', role: 'dataContributor' }
  { unit: 'payments-processor', database: 'payments', role: 'dataContributor' }
  { unit: 'shipping-processor', database: 'shipping', role: 'dataContributor' }
  { unit: 'notifications-processor', database: 'notifications', role: 'dataContributor' }
  { unit: 'readmodels-projections', database: 'readmodels', role: 'dataContributor' }
  // WHY: the read-side API has read access only. Even if compromised it cannot change order views.
  { unit: 'readmodels-api', database: 'readmodels', role: 'dataReader' }
  { unit: 'analytics-processor', database: 'analytics', role: 'dataContributor' }
]

module cosmos '../modules/data/cosmos.bicep' = {
  name: 'cosmos'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: location
    tags: dataTags
    serverless: config.cosmos.serverless
    maxAutoscaleThroughput: config.cosmos.maxAutoscaleThroughput
    zoneRedundant: config.zoneRedundant
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    databases: cosmosDatabases
    dataRoleAssignments: map(cosmosAccess, access => {
      principalId: identities[access.unit].principalId
      database: access.database
      role: access.role
    })
  }
}

// --- Azure SQL ------------------------------------------------------------------------------------------------

module sql '../modules/data/sql.bicep' = {
  name: 'sql'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: location
    tags: dataTags
    skuName: config.sql.skuName
    autoPauseDelayMinutes: config.sql.autoPauseDelayMinutes
    zoneRedundant: config.zoneRedundant
    adminGroupObjectId: config.sqlAdminGroupObjectId
    adminGroupName: config.sqlAdminGroupName
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
  }
}

// --- Azure Managed Redis --------------------------------------------------------------------------------------

module redis '../modules/data/redis.bicep' = {
  name: 'redis'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: location
    tags: dataTags
    skuName: config.redis.skuName
    highAvailability: config.redis.highAvailability
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    accessPolicyAssignments: map(['orders-api', 'inventory-api', 'inventory-worker', 'readmodels-api', 'readmodels-projections'], unit => {
      name: unit
      principalId: identities[unit].principalId
    })
  }
}

// --- Storage --------------------------------------------------------------------------------------------------

module docsStorage '../modules/data/storage.bicep' = {
  name: 'storage-docs'
  params: {
    name: compactName('st', 'docs', config.name, config.regionCode)
    location: location
    tags: dataTags
    zoneRedundant: config.zoneRedundant
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    containerNames: ['invoices', 'labels', 'eventgrid-deadletter']
    allowTrustedAzureServices: true // dead-letter Event Grid
    dataProtection: true
    lifecycleRules: [
      {
        enabled: true
        name: 'invoices-retention'
        type: 'Lifecycle'
        definition: {
          filters: { blobTypes: ['blockBlob'], prefixMatch: ['invoices/'] }
          // WHY: invoices are read mostly in the first month (e-mail, complaints). After that cheaper and cheaper tiers, and deletion
          // after 6 years matches the retention period for accounting documents.
          actions: {
            baseBlob: {
              tierToCool: { daysAfterModificationGreaterThan: 30 }
              tierToCold: { daysAfterModificationGreaterThan: 180 }
              tierToArchive: { daysAfterModificationGreaterThan: 365 }
              delete: { daysAfterModificationGreaterThan: 2190 }
            }
            version: { delete: { daysAfterCreationGreaterThan: 90 } }
          }
        }
      }
      {
        enabled: true
        name: 'labels-retention'
        type: 'Lifecycle'
        definition: {
          filters: { blobTypes: ['blockBlob'], prefixMatch: ['labels/'] }
          actions: {
            baseBlob: {
              tierToCool: { daysAfterModificationGreaterThan: 7 }
              delete: { daysAfterModificationGreaterThan: 90 }
            }
          }
        }
      }
    ]
    accountRoleAssignments: [
      // Storage Blob Delegator: the right to get a delegation key for user delegation SAS (links to labels and invoices).
      { principalId: identities['shipping-processor'].principalId, role: builtInRoles.storageBlobDelegator }
      { principalId: identities['notifications-processor'].principalId, role: builtInRoles.storageBlobDelegator }
      { principalId: identities['readmodels-api'].principalId, role: builtInRoles.storageBlobDelegator }
    ]
    containerRoleAssignments: [
      { container: 'invoices', principalId: identities['payments-processor'].principalId, role: builtInRoles.storageBlobDataContributor }
      { container: 'invoices', principalId: identities['notifications-processor'].principalId, role: builtInRoles.storageBlobDataReader }
      { container: 'invoices', principalId: identities['readmodels-api'].principalId, role: builtInRoles.storageBlobDataReader }
      { container: 'labels', principalId: identities['shipping-processor'].principalId, role: builtInRoles.storageBlobDataContributor }
      { container: 'eventgrid-deadletter', principalId: platformIdentities['eventgrid-docs'].principalId, role: builtInRoles.storageBlobDataContributor }
    ]
  }
}

var functionUnits = [
  'orders-processors'
  'order-saga'
  'payments-processor'
  'shipping-processor'
  'notifications-processor'
  'readmodels-projections'
  'analytics-processor'
]

module functionsStorage '../modules/data/storage.bicep' = {
  name: 'storage-func'
  params: {
    name: compactName('st', 'func', config.name, config.regionCode)
    location: location
    tags: tags(config.name, 'platform-functions', 'internal')
    zoneRedundant: config.zoneRedundant
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    // AZURE: Flex Consumption deploys the package from a blob container (one per application), not from a zip file in the settings.
    containerNames: map(functionUnits, unit => 'deploy-${unit}')
    // AZURE: the Functions host uses the AzureWebJobsStorage account for leases, timers and Event Hubs checkpoints.
    // With an identity (AzureWebJobsStorage__accountName) it needs roles on blob, queue and table.
    accountRoleAssignments: flatten(map(functionUnits, unit => [
      { principalId: identities[unit].principalId, role: builtInRoles.storageBlobDataOwner }
      { principalId: identities[unit].principalId, role: builtInRoles.storageQueueDataContributor }
      { principalId: identities[unit].principalId, role: builtInRoles.storageTableDataContributor }
    ]))
  }
}

module lakeStorage '../modules/data/storage.bicep' = {
  name: 'storage-lake'
  params: {
    name: compactName('st', 'lake', config.name, config.regionCode)
    location: location
    tags: tags(config.name, 'analytics', 'confidential')
    zoneRedundant: config.zoneRedundant
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    hierarchicalNamespace: true
    allowTrustedAzureServices: true // Event Hubs Capture
    containerNames: ['capture']
    lifecycleRules: [
      {
        enabled: true
        name: 'capture-tiering'
        type: 'Lifecycle'
        definition: {
          filters: { blobTypes: ['blockBlob'], prefixMatch: ['capture/'] }
          actions: {
            baseBlob: {
              tierToCool: { daysAfterModificationGreaterThan: 30 }
              tierToArchive: { daysAfterModificationGreaterThan: 180 }
            }
          }
        }
      }
    ]
    containerRoleAssignments: [
      // The role is granted before the hub is created: Capture with an identity checks access when it is configured.
      { container: 'capture', principalId: platformIdentities['evh-capture'].principalId, role: builtInRoles.storageBlobDataContributor }
    ]
  }
}

// --- Configuration and secrets ------------------------------------------------------------------------------------

module keyVault '../modules/config/key-vault.bicep' = {
  name: 'key-vault'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: location
    tags: dataTags
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    secretReaders: [
      { principalId: identities['payments-processor'].principalId, principalType: 'ServicePrincipal' }
      { principalId: identities['shipping-processor'].principalId, principalType: 'ServicePrincipal' }
      // APIM: the carrier webhook token (named value) for verification at the gateway.
      { principalId: platformIdentities.apim.principalId, principalType: 'ServicePrincipal' }
    ]
  }
}

module appConfiguration '../modules/config/app-configuration.bicep' = {
  name: 'app-configuration'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: location
    tags: dataTags
    keyVaultUri: keyVault.outputs.uri
    readerPrincipalIds: map(deployableUnits, unit => identities[unit].principalId)
    // The "Shared:" prefix and the service prefix are trimmed on read (AddShopFlowAzure), so
    // "Orders:Authentication:Customers:Audience" reaches orders-api as "Authentication:Customers:Audience".
    settings: [
      { key: 'Shared:Authentication:Customers:Authority', value: config.entra.customersAuthority }
      { key: 'Shared:Authentication:Workforce:TenantId', value: config.entra.workforceTenantId }
      { key: 'Orders:Authentication:Customers:Audience', value: config.entra.apiAudiences.orders }
      { key: 'Orders:Authentication:Workforce:Audiences:0', value: config.entra.apiAudiences.orders }
      { key: 'Inventory:Authentication:Workforce:Audiences:0', value: config.entra.apiAudiences.inventory }
      { key: 'ReadModels:Authentication:Customers:Audience', value: config.entra.apiAudiences.readmodels }
      { key: 'Notifications:CustomerDirectory:TenantId', value: config.entra.customersTenantId }
      { key: 'Notifications:CustomerDirectory:ClientId', value: config.entra.customerDirectoryClientId }
      { key: 'Orders:Catalog:StalePriceTolerance', value: '00:15:00' }
      { key: 'Notifications:InvoiceLinks:Lifetime', value: '3.00:00:00' }
      { key: 'ReadModels:OrderDetailsCache:TimeToLive', value: '00:01:00' }
    ]
  }
}

module communication '../modules/communication/communication-services.bicep' = {
  name: 'communication'
  params: {
    envName: config.name
    tags: dataTags
    publicDomain: config.publicDomain
    senderPrincipalId: identities['notifications-processor'].principalId
  }
}

// --- Private endpoints --------------------------------------------------------------------------------------------

// WHY: two structures instead of one. The array a module loop iterates over must be known at the start of the deployment,
// while resource identifiers are outputs of other modules (known only during it). The loop therefore goes over a fixed
// list and takes the target identifier from a map.
var endpointTargets = {
  cosmos: cosmos.outputs.id
  sql: sql.outputs.serverId
  redis: redis.outputs.id
  docs: docsStorage.outputs.id
  func: functionsStorage.outputs.id
  lake: lakeStorage.outputs.id
  kv: keyVault.outputs.id
  appcs: appConfiguration.outputs.id
}

var privateEndpoints = [
  { name: 'cosmos', target: 'cosmos', groupId: 'Sql', zone: 'privatelink.documents.azure.com' }
  { name: 'sql', target: 'sql', groupId: 'sqlServer', zone: 'privatelink.database.windows.net' }
  { name: 'redis', target: 'redis', groupId: 'redisEnterprise', zone: 'privatelink.redis.azure.net' }
  { name: 'docs-blob', target: 'docs', groupId: 'blob', zone: 'privatelink.blob.${storageSuffix}' }
  { name: 'func-blob', target: 'func', groupId: 'blob', zone: 'privatelink.blob.${storageSuffix}' }
  { name: 'func-queue', target: 'func', groupId: 'queue', zone: 'privatelink.queue.${storageSuffix}' }
  { name: 'func-table', target: 'func', groupId: 'table', zone: 'privatelink.table.${storageSuffix}' }
  { name: 'lake-blob', target: 'lake', groupId: 'blob', zone: 'privatelink.blob.${storageSuffix}' }
  { name: 'lake-dfs', target: 'lake', groupId: 'dfs', zone: 'privatelink.dfs.${storageSuffix}' }
  { name: 'kv', target: 'kv', groupId: 'vault', zone: 'privatelink.vaultcore.azure.net' }
  { name: 'appcs', target: 'appcs', groupId: 'configurationStores', zone: 'privatelink.azconfig.io' }
]

module endpoints '../modules/network/private-endpoint.bicep' = [for endpoint in privateEndpoints: {
  name: 'pe-${endpoint.name}'
  params: {
    name: 'pe-shopflow-${endpoint.name}-${config.name}-${config.regionCode}'
    location: location
    tags: dataTags
    subnetId: subnetIds['snet-pe']
    targetResourceId: endpointTargets[endpoint.target]
    groupId: endpoint.groupId
    privateDnsZoneId: privateDnsZoneIds[endpoint.zone]
  }
}]

output cosmosAccountId string = cosmos.outputs.id
output cosmosEndpoint string = cosmos.outputs.endpoint
output sqlServerFqdn string = sql.outputs.serverFqdn
output sqlDatabaseName string = sql.outputs.databaseName
output redisEndpoint string = redis.outputs.endpoint
output docsStorageId string = docsStorage.outputs.id
output docsBlobEndpoint string = docsStorage.outputs.blobEndpoint
output functionsStorageName string = functionsStorage.outputs.name
output functionsBlobEndpoint string = functionsStorage.outputs.blobEndpoint
output lakeStorageId string = lakeStorage.outputs.id
output keyVaultUri string = keyVault.outputs.uri
output keyVaultName string = keyVault.outputs.name
output appConfigurationEndpoint string = appConfiguration.outputs.endpoint
output communicationEndpoint string = communication.outputs.endpoint
output emailSenderAddress string = communication.outputs.senderAddress
