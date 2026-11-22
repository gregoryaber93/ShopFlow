// Azure SQL: a server with Entra ID-only sign-in and the Inventory database.

import { resourceName } from '../../shared/naming.bicep'

param envName string
param regionCode string
param location string
param tags object
param skuName string
param autoPauseDelayMinutes int
param zoneRedundant bool
param adminGroupObjectId string
param adminGroupName string
param logAnalyticsWorkspaceId string

resource server 'Microsoft.Sql/servers@2021-11-01' = {
  name: resourceName('sql', '', envName, regionCode)
  location: location
  tags: tags
  // AZURE: the server identity is needed for "CREATE USER [id-...] FROM EXTERNAL PROVIDER": the server checks in Entra ID
  // that the application's managed identity exists. It requires the Directory Readers role for this identity.
  identity: { type: 'SystemAssigned' }
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    // WHY: no SQL login and no password. The administrator is an Entra ID group (membership managed by PIM), and
    // applications connect with a managed identity token (Authentication=Active Directory Managed Identity).
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: adminGroupName
      sid: adminGroupObjectId
      tenantId: tenant().tenantId
      principalType: 'Group'
    }
  }
}

var isServerless = startsWith(skuName, 'GP_S_')

resource database 'Microsoft.Sql/servers/databases@2021-11-01' = {
  parent: server
  name: 'sqldb-inventory'
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: 'GeneralPurpose'
  }
  properties: {
    zoneRedundant: zoneRedundant
    // AZURE: serverless pauses the database after a period of inactivity (we then pay only for storage). The first connection
    // after a pause takes tens of seconds, which is why EF Core has EnableRetryOnFailure and prod uses provisioned.
    autoPauseDelay: isServerless ? autoPauseDelayMinutes : null
    minCapacity: isServerless ? json('0.5') : null
    requestedBackupStorageRedundancy: zoneRedundant ? 'Zone' : 'Local'
  }
}

resource auditing 'Microsoft.Sql/servers/auditingSettings@2021-11-01' = {
  parent: server
  name: 'default'
  properties: {
    state: 'Enabled'
    isAzureMonitorTargetEnabled: true
  }
}

resource databaseDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: database
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      { category: 'SQLSecurityAuditEvents', enabled: true }
      { category: 'QueryStoreRuntimeStatistics', enabled: true }
      { category: 'Errors', enabled: true }
      { category: 'Deadlocks', enabled: true }
    ]
    metrics: [{ category: 'Basic', enabled: true }]
  }
}

output serverId string = server.id
output serverFqdn string = server.properties.fullyQualifiedDomainName
output databaseName string = database.name
