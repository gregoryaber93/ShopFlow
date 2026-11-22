// API Management Standard v2: the gateway for customers and partners, with VNet integration towards private backends.

import { environmentConfig } from '../../shared/types.bicep'
import { resourceName } from '../../shared/naming.bicep'

param config environmentConfig
param tags object
param subnetId string
param identityId string
param identityClientId string
param keyVaultUri string
param logAnalyticsWorkspaceId string

@description('Backend addresses from the apps layer.')
param backends object

@description('AZURE: public access cannot be disabled when the gateway is created; a second pipeline pass sets Disabled after the Private Link connection from Front Door is approved.')
@allowed(['Enabled', 'Disabled'])
param publicNetworkAccess string = 'Enabled'

resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: resourceName('apim', '', config.name, config.regionCode)
  location: config.location
  tags: tags
  sku: {
    name: 'StandardV2'
    capacity: config.apim.capacity
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    publisherEmail: config.alertEmail
    publisherName: 'ShopFlow'
    // AZURE: in the Standard v2 tier "External" with a subnet means outbound integration: the gateway calls backends through
    // the VNet (App Service and function private endpoints, internal Container Apps). Inbound traffic comes from Front Door.
    virtualNetworkType: 'External'
    virtualNetworkConfiguration: { subnetResourceId: subnetId }
    publicNetworkAccess: publicNetworkAccess
  }
}

// --- Named values ------------------------------------------------------------------------------------------------

var plainValues = {
  'customers-authority': config.entra.customersAuthority
  'orders-audience': config.entra.apiAudiences.orders
  'apim-identity-client-id': identityClientId
  'analytics-function-client-id': config.entra.functionAuthClientIds['analytics-processor']
  'shipping-function-client-id': config.entra.functionAuthClientIds['shipping-processor']
  'payments-function-client-id': config.entra.functionAuthClientIds['payments-processor']
  'psp-ip-from': config.partners.pspIpFrom
  'psp-ip-to': config.partners.pspIpTo
  'carrier-ip-from': config.partners.carrierIpFrom
  'carrier-ip-to': config.partners.carrierIpTo
}

resource namedValues 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = [for value in items(plainValues): {
  parent: apim
  name: value.key
  properties: {
    displayName: value.key
    value: value.value
    secret: false
  }
}]

// AZURE: a named value linked to a Key Vault secret. The gateway fetches the value with its identity and refreshes it
// every few hours, so rotating the carrier token is a new secret version, without an APIM deployment.
resource carrierToken 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'carrier-webhook-token'
  properties: {
    displayName: 'carrier-webhook-token'
    secret: true
    keyVault: {
      secretIdentifier: '${keyVaultUri}secrets/carrier-webhook-token'
      identityClientId: identityClientId
    }
  }
}

// --- API -------------------------------------------------------------------------------------------------------

func customerPolicy(template string, audience string, callsPerMinute int) string =>
  replace(replace(template, '__API_AUDIENCE__', audience), '__CALLS_PER_MINUTE__', string(callsPerMinute))

var customerTemplate = loadTextContent('../../apim/policies/customer-api.xml')

var apis = [
  {
    // /shop/v1/orders, /shop/v2/orders, /shop/v1/cart → orders-api
    name: 'shop'
    path: 'shop'
    serviceUrl: backends.ordersApi
    methods: ['GET', 'POST', 'PUT', 'DELETE']
    urlTemplate: '/*'
    policy: customerPolicy(customerTemplate, config.entra.apiAudiences.orders, 120)
  }
  {
    // /account/orders → readmodels-api /v1/me/orders. A higher limit: the application polls the status after placing an order.
    name: 'account'
    path: 'account'
    serviceUrl: '${backends.readModelsApi}/v1/me'
    methods: ['GET']
    urlTemplate: '/*'
    policy: customerPolicy(customerTemplate, config.entra.apiAudiences.readmodels, 600)
  }
  {
    name: 'catalog'
    path: 'catalog'
    serviceUrl: '${backends.inventoryApi}/v1/catalog'
    methods: ['GET']
    urlTemplate: '/*'
    policy: loadTextContent('../../apim/policies/catalog.xml')
  }
  {
    name: 'clickstream'
    path: 'collect'
    serviceUrl: '${backends.analyticsFunctions}/clickstream'
    methods: ['POST']
    urlTemplate: '/'
    policy: loadTextContent('../../apim/policies/clickstream.xml')
  }
  {
    name: 'psp-webhooks'
    path: 'webhooks/psp'
    serviceUrl: '${backends.paymentsFunctions}/webhooks/psp'
    methods: ['POST']
    urlTemplate: '/'
    policy: loadTextContent('../../apim/policies/psp-webhook.xml')
  }
  {
    name: 'carrier-webhooks'
    path: 'webhooks/carrier'
    serviceUrl: '${backends.shippingFunctions}/webhooks/carrier'
    methods: ['POST']
    urlTemplate: '/'
    policy: loadTextContent('../../apim/policies/carrier-webhook.xml')
  }
]

resource apiResources 'Microsoft.ApiManagement/service/apis@2024-05-01' = [for api in apis: {
  parent: apim
  name: api.name
  properties: {
    displayName: api.name
    path: api.path
    serviceUrl: api.serviceUrl
    protocols: ['https']
    // WHY: no subscription keys. Customers authenticate with an Entra ID token, partners with a token/signature checked in a
    // policy. A subscription key in a browser application would not be a secret anyway.
    subscriptionRequired: false
    apiRevision: '1'
  }
}]

var operations = flatten(map(range(0, length(apis)), i => map(apis[i].methods, method => {
  apiIndex: i
  method: method
  urlTemplate: apis[i].urlTemplate
})))

resource apiOperations 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = [for operation in operations: {
  parent: apiResources[operation.apiIndex]
  name: toLower(operation.method)
  properties: {
    displayName: '${operation.method} ${operation.urlTemplate}'
    method: operation.method
    urlTemplate: operation.urlTemplate
  }
}]

resource apiPolicies 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = [for (api, i) in apis: {
  parent: apiResources[i]
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: api.policy
  }
  dependsOn: [namedValues, carrierToken]
}]

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: apim
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [{ category: 'GatewayLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output id string = apim.id
output name string = apim.name
output gatewayHostName string = replace(apim.properties.gatewayUrl, 'https://', '')
