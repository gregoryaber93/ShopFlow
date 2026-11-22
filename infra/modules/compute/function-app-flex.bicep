// Function app on the Flex Consumption plan: .NET 10 isolated, user-assigned identity, VNet integration.

param name string
param location string
param tags object
param identityId string
param identityClientId string
param integrationSubnetId string
param logAnalyticsWorkspaceId string

@description('Host storage account (AzureWebJobsStorage, deployment packages, Event Hubs checkpoints).')
param storageAccountName string
param storageBlobEndpoint string
param deploymentContainer string

param maximumInstanceCount int
param instanceMemoryMB int = 2048

@description('Always-ready instances for the HTTP function group (0 = scale from zero).')
param alwaysReadyHttpInstances int = 0

@description('Application settings (name → value).')
param appSettings object

@description('Easy Auth: clientId of the app registration; empty = no platform authentication (functions without HTTP).')
param authClientId string = ''
param authTenantId string = ''

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: replace(name, 'func-', 'asp-')
  location: location
  tags: tags
  kind: 'functionapp'
  // AZURE: Flex Consumption (FC1): per-function scaling from zero, VNet integration and always-ready instances.
  // Unlike the classic Consumption plan it has private networking and configurable instance memory.
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: { reserved: true }
}

// AZURE: identity-based connections. The Functions host (triggers) reads the <connection>__credential and
// <connection>__clientId settings; application code uses AZURE_CLIENT_ID. No setting contains a key or a password.
var identitySettings = {
  AZURE_CLIENT_ID: identityClientId
  AzureWebJobsStorage__accountName: storageAccountName
  AzureWebJobsStorage__credential: 'managedidentity'
  AzureWebJobsStorage__clientId: identityClientId
}

resource site 'Microsoft.Web/sites@2024-04-01' = {
  name: name
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Disabled'
    virtualNetworkSubnetId: integrationSubnetId
    functionAppConfig: {
      // The deployment package lives in a blob container and is fetched with the identity (the account has no keys).
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageBlobEndpoint}${deploymentContainer}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identityId
          }
        }
      }
      scaleAndConcurrency: {
        // WHY: the instance cap also protects dependencies. A function calling the carrier API or ACS would turn a traffic spike
        // into an avalanche of 429 responses if scaling were unlimited.
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
        alwaysReady: alwaysReadyHttpInstances > 0 ? [{ name: 'http', instanceCount: alwaysReadyHttpInstances }] : []
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      http20Enabled: true
      appSettings: [for setting in items(union(identitySettings, appSettings)): { name: setting.key, value: setting.value }]
    }
  }
}

// AZURE: Easy Auth validates the Entra ID token before the function code. Only a request with a valid token for this
// application reaches the function, and the roles go into the X-MS-CLIENT-PRINCIPAL header (EasyAuthPrincipal in code).
resource auth 'Microsoft.Web/sites/config@2024-04-01' = if (!empty(authClientId)) {
  parent: site
  name: 'authsettingsV2'
  properties: {
    platform: { enabled: true }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'Return401'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: authClientId
          openIdIssuer: 'https://login.microsoftonline.com/${authTenantId}/v2.0'
        }
        validation: {
          allowedAudiences: ['api://${authClientId}']
        }
      }
    }
  }
}

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: site
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [{ category: 'FunctionAppLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output id string = site.id
output defaultHostName string = site.properties.defaultHostName
