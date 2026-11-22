// App Configuration: settings, feature flags and references to secrets in Key Vault.

import { resourceName } from '../../shared/naming.bicep'
import { builtInRoles } from '../../shared/roles.bicep'

param envName string
param regionCode string
param location string
param tags object
param keyVaultUri string
param readerPrincipalIds array

@description('Plain settings: [{ key, value }]. Keys carry the service prefix, e.g. Orders:Checkout:MaxLines.')
param settings array = []

resource store 'Microsoft.AppConfiguration/configurationStores@2024-05-01' = {
  name: resourceName('appcs', '', envName, regionCode)
  location: location
  tags: tags
  // WHY: the Developer tier in dev (cheap, no SLA), Standard in test and prod (SLA, private endpoint, replication).
  sku: { name: envName == 'dev' ? 'developer' : 'standard' }
  properties: {
    disableLocalAuth: true
    publicNetworkAccess: 'Disabled'
    enablePurgeProtection: envName == 'prod'
    softDeleteRetentionInDays: 7
    // AZURE: with public access disabled, key-values from Bicep (ARM control plane) still reach the store, because ARM
    // forwards them with the deploying identity (pass-through) through Private Link delegation.
    // Without this, deploying settings would require an agent in the VNet.
    dataPlaneProxy: {
      authenticationMode: 'Pass-through'
      privateLinkDelegation: 'Enabled'
    }
  }
}

// AZURE: the sentinel key. Applications watch only this key and reload the whole configuration when its value changes.
// This allows changing several settings at once and publishing them in one move.
resource sentinel 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-05-01' = {
  parent: store
  name: 'Shared:Sentinel'
  properties: { value: '1' }
}

resource plainSettings 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-05-01' = [for setting in settings: {
  parent: store
  name: setting.key
  properties: { value: setting.value }
}]

// Key Vault references: App Configuration stores only the secret address. The application fetches the value from Key Vault
// with its own identity (Key Vault Secrets User role), so a role on App Configuration alone gives no access to the secret.
var secretReferences = [
  { key: 'Payments:Psp:ApiKey', secret: 'psp-api-key' }
  { key: 'Payments:Psp:WebhookSecrets:0', secret: 'psp-webhook-secret-current' }
  { key: 'Payments:Psp:WebhookSecrets:1', secret: 'psp-webhook-secret-previous' }
  { key: 'Shipping:Carrier:ApiKey', secret: 'carrier-api-key' }
]

resource keyVaultReferences 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-05-01' = [for reference in secretReferences: {
  parent: store
  name: reference.key
  properties: {
    contentType: 'application/vnd.microsoft.appconfig.keyvaultref+json;charset=utf-8'
    // No version: after a refresh (sentinel) the application reads the latest secret version, so a rotation needs no
    // change in App Configuration (secret-rotation runbook).
    value: string({ uri: '${keyVaultUri}secrets/${reference.secret}' })
  }
}]

// Feature flags. The resource name encodes "/" as "~2F" (the key is .appconfig.featureflag/<name>).
var featureFlags = [
  {
    id: 'Orders.PublishOrderSubmittedV1'
    description: 'OrderSubmitted v1 sunset period: Orders publishes both versions.'
    enabled: true
    filters: []
  }
  {
    id: 'Orders.PickupPointDelivery'
    description: 'Pickup-point delivery. Enable only after v1 publishing is switched off (v1 cannot express an order without an address).'
    enabled: false
    filters: []
  }
  {
    id: 'Payments.ForceThreeDSecure'
    description: 'Force 3-D Secure for all payments (e.g. during a wave of fraud).'
    enabled: false
    filters: []
  }
  {
    id: 'Notifications.ShipmentSms'
    description: 'SMS on parcel dispatch for a share of customers (targeting, assignment from a hash of the customer id).'
    enabled: true
    filters: [
      {
        name: 'Microsoft.Targeting'
        parameters: {
          Audience: {
            Users: []
            Groups: []
            DefaultRolloutPercentage: envName == 'prod' ? 10 : 100
          }
        }
      }
    ]
  }
]

resource flags 'Microsoft.AppConfiguration/configurationStores/keyValues@2024-05-01' = [for flag in featureFlags: {
  parent: store
  name: '.appconfig.featureflag~2F${flag.id}'
  properties: {
    contentType: 'application/vnd.microsoft.appconfig.ff+json;charset=utf-8'
    value: string({
      id: flag.id
      description: flag.description
      enabled: flag.enabled
      conditions: { client_filters: flag.filters }
    })
  }
}]

resource readers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in readerPrincipalIds: {
  name: guid(store.id, principalId, builtInRoles.appConfigurationDataReader)
  scope: store
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', builtInRoles.appConfigurationDataReader)
  }
}]

output id string = store.id
output endpoint string = store.properties.endpoint
