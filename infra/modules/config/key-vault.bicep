// Key Vault for secrets of external providers (PSP, carrier). Internal secrets do not exist:
// communication between Azure services uses managed identities.

import { resourceName } from '../../shared/naming.bicep'
import { builtInRoles } from '../../shared/roles.bicep'

param envName string
param regionCode string
param location string
param tags object
param logAnalyticsWorkspaceId string

@description('Identities that read secrets: [{ principalId, principalType }].')
param secretReaders array

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: resourceName('kv', '', envName, regionCode)
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    // AZURE: authorization through Azure RBAC instead of access policies. Permissions are visible in one place with the other
    // roles, fall under PIM and can be granted on a single secret.
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    // Purge protection is irreversible; in prod it protects against permanent deletion of secrets (also by an attacker).
    enablePurgeProtection: envName == 'prod' ? true : null
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
    }
  }
}

// WHY: the role is on the whole vault, not on a secret. The operator creates the secrets (bootstrap), so they may not exist
// yet at deployment time. After they are created a script narrows access to individual secrets (scope .../secrets/<name>).
resource readers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for reader in secretReaders: {
  name: guid(vault.id, reader.principalId, builtInRoles.keyVaultSecretsUser)
  scope: vault
  properties: {
    principalId: reader.principalId
    principalType: reader.principalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', builtInRoles.keyVaultSecretsUser)
  }
}]

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: vault
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [{ categoryGroup: 'audit', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output id string = vault.id
output name string = vault.name
output uri string = vault.properties.vaultUri
