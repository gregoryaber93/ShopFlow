// A Storage account without keys and without public access, with containers, lifecycle rules and per-container roles.

param name string
param location string
param tags object
param zoneRedundant bool
param logAnalyticsWorkspaceId string

@description('ADLS Gen2 (hierarchical namespace), e.g. for Event Hubs Capture.')
param hierarchicalNamespace bool = false

param containerNames array

@description('Blob versioning and soft delete (business documents).')
param dataProtection bool = false

@description('Allows trusted Azure services (Event Grid dead-letter, Event Hubs Capture) despite the closed network.')
param allowTrustedAzureServices bool = false

@description('Lifecycle rules in the ARM format (rules[]).')
param lifecycleRules array = []

@description('Roles on the account: [{ principalId, role }]. role = built-in role identifier.')
param accountRoleAssignments array = []

@description('Roles on a container: [{ container, principalId, role }].')
param containerRoleAssignments array = []

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: zoneRedundant ? 'Standard_ZRS' : 'Standard_LRS' }
  properties: {
    isHnsEnabled: hierarchicalNamespace
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    // AZURE: account keys disabled. A key is full access to everything with no trace of who used it; what remains is
    // Entra ID (data-plane roles) and user delegation SAS signed with an identity token.
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    // AZURE: "Disabled" blocks everything outside private endpoints, including trusted Microsoft services. Event Grid
    // (dead-letter) and Event Hubs Capture write to the account from the platform infrastructure, not from our VNet. For such
    // accounts we keep the public endpoint with a default deny and an exception for trusted services authenticated with a
    // managed identity: it is still impossible to connect from the internet.
    publicNetworkAccess: allowTrustedAzureServices ? 'Enabled' : 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: allowTrustedAzureServices ? 'AzureServices' : 'None'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: account
  name: 'default'
  properties: dataProtection
    ? {
        isVersioningEnabled: true
        changeFeed: { enabled: true }
        deleteRetentionPolicy: { enabled: true, days: 30 }
        containerDeleteRetentionPolicy: { enabled: true, days: 30 }
        restorePolicy: { enabled: true, days: 29 }
      }
    : {
        deleteRetentionPolicy: { enabled: true, days: 7 }
      }
}

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for container in containerNames: {
  parent: blobService
  name: container
  properties: { publicAccess: 'None' }
}]

resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = if (!empty(lifecycleRules)) {
  parent: account
  name: 'default'
  properties: {
    policy: { rules: lifecycleRules }
  }
}

resource accountRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for assignment in accountRoleAssignments: {
  name: guid(account.id, assignment.principalId, assignment.role)
  scope: account
  properties: {
    principalId: assignment.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', assignment.role)
  }
}]

// WHY: the role is on the container, not the account. Notifications reads invoices (invoices) but not carrier labels
// (labels), even though both containers are in the same account.
resource containerRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for assignment in containerRoleAssignments: {
  name: guid(account.id, assignment.container, assignment.principalId, assignment.role)
  scope: containers[indexOf(containerNames, assignment.container)]
  properties: {
    principalId: assignment.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', assignment.role)
  }
}]

resource blobDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: blobService
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      { category: 'StorageRead', enabled: true }
      { category: 'StorageWrite', enabled: true }
      { category: 'StorageDelete', enabled: true }
    ]
    metrics: [{ category: 'Transaction', enabled: true }]
  }
}

output id string = account.id
output name string = account.name
output blobEndpoint string = account.properties.primaryEndpoints.blob
