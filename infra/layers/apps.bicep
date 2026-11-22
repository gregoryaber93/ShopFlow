// Apps layer (rg-shopflow-{env}-weu-apps): twelve deployable units.
// Application settings contain only addresses and names, never keys: every dependency is authenticated with a managed
// identity, and the secrets of external providers come from Key Vault through App Configuration.

import { environmentConfig } from '../shared/types.bicep'
import { resourceName, tags } from '../shared/naming.bicep'

param config environmentConfig
param subnetIds object
param privateDnsZoneIds object
param vnetId string
param natPublicIpAddress string
param logAnalyticsWorkspaceId string
param appInsightsConnectionString string
param identities object

param acrName string
param acrResourceGroup string
@description('Image tag (commit SHA) for an application missing from imageTags: the same image goes through dev, test and prod.')
param imageTag string
@description('Image tags of the running container applications (image name → tag), computed by the pipeline.')
param imageTags object

param cosmosEndpoint string
param sqlServerFqdn string
param sqlDatabaseName string
param redisEndpoint string
param docsBlobEndpoint string
param functionsStorageName string
param functionsBlobEndpoint string
param appConfigurationEndpoint string
param serviceBusNamespace string
param eventHubsNamespace string
param communicationEndpoint string
param emailSenderAddress string

var location = config.location
var appTags = tags(config.name, 'platform-apps', 'confidential')
var filesBaseUrl = 'https://files.${config.publicDomain}'
var shopBaseUrl = 'https://${config.name == 'prod' ? '' : '${config.name}.'}${config.publicDomain}/'

// Shared by all hosts.
func commonSettings(unit string, keyPrefix string, connectionString string, appConfigEndpoint string) object => {
  APPLICATIONINSIGHTS_CONNECTION_STRING: connectionString
  OTEL_SERVICE_NAME: unit
  AppConfiguration__Endpoint: appConfigEndpoint
  AppConfiguration__KeyPrefix: keyPrefix
}

// Identity-based Functions trigger connections: <name>__<address>, __credential, __clientId.
func identityConnection(connection string, addressKey string, address string, clientId string) object => {
  '${connection}__${addressKey}': address
  '${connection}__credential': 'managedidentity'
  '${connection}__clientId': clientId
}

func cosmosSettings(endpoint string, database string, clientId string) object => union(
  identityConnection('Cosmos', 'accountEndpoint', endpoint, clientId),
  { Cosmos__DatabaseName: database })

// --- Container registry ---------------------------------------------------------------------------------------------

var containerUnits = ['inventory-api', 'inventory-worker', 'inventory-migrations', 'readmodels-api']

module acrPull '../modules/identity/acr-pull.bicep' = {
  name: 'acr-pull-${config.name}'
  scope: resourceGroup(acrResourceGroup)
  params: {
    registryName: acrName
    principalIds: map(containerUnits, unit => identities[unit].principalId)
  }
}

// --- orders-api (App Service) -----------------------------------------------------------------------------------------

module containerEnvironment '../modules/compute/container-apps-environment.bicep' = {
  name: 'container-apps-environment'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: location
    tags: appTags
    infrastructureSubnetId: subnetIds['snet-aca']
    zoneRedundant: config.zoneRedundant
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
  }
}

module containerDns '../modules/network/container-apps-dns.bicep' = {
  name: 'container-apps-dns'
  params: {
    defaultDomain: containerEnvironment.outputs.defaultDomain
    staticIp: containerEnvironment.outputs.staticIp
    vnetId: vnetId
    tags: appTags
  }
}

var inventoryApiName = resourceName('ca', 'inventory-api', config.name, config.regionCode)

module ordersApi '../modules/compute/app-service.bicep' = {
  name: 'orders-api'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: location
    tags: tags(config.name, 'orders', 'confidential')
    skuName: config.appService.skuName
    minInstances: config.appService.minInstances
    maxInstances: config.appService.maxInstances
    zoneRedundant: config.zoneRedundant
    identityId: identities['orders-api'].id
    integrationSubnetId: subnetIds['snet-appsvc']
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    appSettings: union(
      commonSettings('orders-api', 'Orders:', appInsightsConnectionString, appConfigurationEndpoint),
      {
        AZURE_CLIENT_ID: identities['orders-api'].clientId
        Cosmos__accountEndpoint: cosmosEndpoint
        Cosmos__DatabaseName: 'orders'
        Redis__Endpoint: redisEndpoint
        // Service-to-service call through the VNet: a name in the Container Apps environment domain (private zone).
        Catalog__InventoryApiBaseUrl: 'https://${inventoryApiName}.${containerEnvironment.outputs.defaultDomain}/'
        Catalog__Scope: '${config.entra.apiAudiences.inventory}/.default'
        WEBSITE_RUN_FROM_PACKAGE: '1'
      })
  }
}

// --- Container Apps -----------------------------------------------------------------------------------------------

var registryServer = '${acrName}.azurecr.io'

// WHY: the application pipeline changes the application version (az containerapp update --image), not the infrastructure
// deployment. If Bicep always set one tag, every infrastructure change would roll applications back to the version from
// that commit. The pipeline therefore passes the tags of the currently running images, and imageTag applies only to new applications.
var images = toObject(
  ['inventory-api', 'inventory-worker', 'inventory-migrations', 'readmodels-api'],
  name => name,
  name => '${registryServer}/${name}:${imageTags[?name] ?? imageTag}')
var sqlConnection = 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Encrypt=True;Authentication=Active Directory Managed Identity;'

module inventoryApi '../modules/compute/container-app.bicep' = {
  name: 'inventory-api'
  params: {
    name: inventoryApiName
    location: location
    tags: tags(config.name, 'inventory', 'internal')
    environmentId: containerEnvironment.outputs.id
    identityId: identities['inventory-api'].id
    identityClientId: identities['inventory-api'].clientId
    image: images['inventory-api']
    registryServer: registryServer
    ingressEnabled: true
    minReplicas: config.name == 'prod' ? 2 : 0
    maxReplicas: 10
    environmentVariables: union(
      commonSettings('inventory-api', 'Inventory:', appInsightsConnectionString, appConfigurationEndpoint),
      {
        Sql__ConnectionString: '${sqlConnection}User Id=${identities['inventory-api'].clientId};'
        Redis__Endpoint: redisEndpoint
      })
    scaleRules: [
      {
        // AZURE: HTTP scaling by the number of concurrent requests per replica; with zero requests the application drops to minReplicas.
        name: 'http'
        http: { metadata: { concurrentRequests: '50' } }
      }
    ]
  }
  dependsOn: [acrPull]
}

module inventoryWorker '../modules/compute/container-app.bicep' = {
  name: 'inventory-worker'
  params: {
    name: resourceName('ca', 'inventory-worker', config.name, config.regionCode)
    location: location
    tags: tags(config.name, 'inventory', 'internal')
    environmentId: containerEnvironment.outputs.id
    identityId: identities['inventory-worker'].id
    identityClientId: identities['inventory-worker'].clientId
    image: images['inventory-worker']
    registryServer: registryServer
    ingressEnabled: false
    minReplicas: 0
    maxReplicas: 20
    environmentVariables: union(
      commonSettings('inventory-worker', 'Inventory:', appInsightsConnectionString, appConfigurationEndpoint),
      {
        Sql__ConnectionString: '${sqlConnection}User Id=${identities['inventory-worker'].clientId};'
        Redis__Endpoint: redisEndpoint
        ServiceBus__FullyQualifiedNamespace: serviceBusNamespace
      })
    scaleRules: [
      {
        // AZURE: KEDA scales by the length of the inventory-commands queue: one replica per messageCount messages
        // (equal to the processor's MaxConcurrentCalls), down to zero when the queue is empty. The scaler signs in with the application identity.
        name: 'inventory-commands'
        custom: {
          type: 'azure-servicebus'
          metadata: {
            namespace: split(serviceBusNamespace, '.')[0]
            queueName: 'inventory-commands'
            messageCount: '16'
          }
          identity: identities['inventory-worker'].id
        }
      }
    ]
  }
  dependsOn: [acrPull]
}

module inventoryMigrations '../modules/compute/container-app-job.bicep' = {
  name: 'inventory-migrations'
  params: {
    name: resourceName('caj', 'inventory-migrations', config.name, config.regionCode)
    location: location
    tags: tags(config.name, 'inventory', 'internal')
    environmentId: containerEnvironment.outputs.id
    identityId: identities['inventory-migrations'].id
    identityClientId: identities['inventory-migrations'].clientId
    image: images['inventory-migrations']
    registryServer: registryServer
    command: ['./efbundle', '--connection', '${sqlConnection}User Id=${identities['inventory-migrations'].clientId};']
    environmentVariables: {}
  }
  dependsOn: [acrPull]
}

module readModelsApi '../modules/compute/container-app.bicep' = {
  name: 'readmodels-api'
  params: {
    name: resourceName('ca', 'readmodels-api', config.name, config.regionCode)
    location: location
    tags: tags(config.name, 'readmodels', 'confidential')
    environmentId: containerEnvironment.outputs.id
    identityId: identities['readmodels-api'].id
    identityClientId: identities['readmodels-api'].clientId
    image: images['readmodels-api']
    registryServer: registryServer
    ingressEnabled: true
    minReplicas: config.name == 'prod' ? 2 : 0
    maxReplicas: 10
    environmentVariables: union(
      commonSettings('readmodels-api', 'ReadModels:', appInsightsConnectionString, appConfigurationEndpoint),
      {
        Cosmos__accountEndpoint: cosmosEndpoint
        Cosmos__DatabaseName: 'readmodels'
        Redis__Endpoint: redisEndpoint
        InvoiceDownloads__serviceUri: docsBlobEndpoint
        BlobLinks__PublicBaseUrl: filesBaseUrl
      })
    scaleRules: [
      {
        name: 'http'
        http: { metadata: { concurrentRequests: '100' } }
      }
    ]
  }
  dependsOn: [acrPull]
}

// --- Durable Task Scheduler -----------------------------------------------------------------------------------------

module durableTaskScheduler '../modules/compute/durable-task-scheduler.bicep' = {
  name: 'durable-task-scheduler'
  params: {
    envName: config.name
    regionCode: config.regionCode
    location: location
    tags: tags(config.name, 'orders', 'confidential')
    sagaPrincipalId: identities['order-saga'].principalId
    allowedEgressIp: natPublicIpAddress
  }
}

// --- Function apps (Flex Consumption) ---------------------------------------------------------------------------------

// Settings known only during the deployment (outputs of other modules). A module loop must iterate over an array known at
// the start, so we keep them in a separate map rather than in the elements of the functionApps array.
var runtimeFunctionSettings = {
  'order-saga': {
    // AZURE: the connection to the Durable Task Scheduler uses an identity only (no key in the connection string).
    DURABLE_TASK_SCHEDULER_CONNECTION_STRING: 'Endpoint=${durableTaskScheduler.outputs.endpoint};Authentication=ManagedIdentity;ClientID=${identities['order-saga'].clientId}'
    TASKHUB_NAME: durableTaskScheduler.outputs.taskHubName
    OrdersApi__BaseUrl: 'https://${ordersApi.outputs.defaultHostName}/'
  }
}

var serviceBus = { fqdn: serviceBusNamespace }

var functionApps = [
  {
    unit: 'orders-processors'
    keyPrefix: 'Orders:'
    http: false
    maxInstances: config.functions.maximumInstanceCount
    settings: union(
      cosmosSettings(cosmosEndpoint, 'orders', identities['orders-processors'].clientId),
      identityConnection('ServiceBus', 'fullyQualifiedNamespace', serviceBus.fqdn, identities['orders-processors'].clientId))
  }
  {
    unit: 'order-saga'
    keyPrefix: 'OrderSaga:'
    http: true
    maxInstances: config.functions.maximumInstanceCount
    settings: union(
      cosmosSettings(cosmosEndpoint, 'saga', identities['order-saga'].clientId),
      identityConnection('ServiceBus', 'fullyQualifiedNamespace', serviceBus.fqdn, identities['order-saga'].clientId),
      { OrdersApi__Scope: '${config.entra.apiAudiences.orders}/.default' })
  }
  {
    unit: 'payments-processor'
    keyPrefix: 'Payments:'
    http: true
    maxInstances: config.functions.maximumInstanceCount
    settings: union(
      cosmosSettings(cosmosEndpoint, 'payments', identities['payments-processor'].clientId),
      identityConnection('ServiceBus', 'fullyQualifiedNamespace', serviceBus.fqdn, identities['payments-processor'].clientId),
      {
        InvoiceStorage__serviceUri: docsBlobEndpoint
        Psp__BaseUrl: config.partners.pspBaseUrl
      })
  }
  {
    unit: 'shipping-processor'
    keyPrefix: 'Shipping:'
    http: true
    // WHY: the limit comes from the carrier API's request limit, not from compute power (see host.json, maxConcurrentCalls).
    maxInstances: 10
    settings: union(
      cosmosSettings(cosmosEndpoint, 'shipping', identities['shipping-processor'].clientId),
      identityConnection('ServiceBus', 'fullyQualifiedNamespace', serviceBus.fqdn, identities['shipping-processor'].clientId),
      {
        LabelStorage__serviceUri: docsBlobEndpoint
        BlobLinks__PublicBaseUrl: filesBaseUrl
        Carrier__BaseUrl: config.partners.carrierBaseUrl
      })
  }
  {
    unit: 'notifications-processor'
    keyPrefix: 'Notifications:'
    http: true
    // WHY: low ACS Email send limits; few instances turn a traffic spike into a queue instead of into 429s.
    maxInstances: 4
    settings: union(
      cosmosSettings(cosmosEndpoint, 'notifications', identities['notifications-processor'].clientId),
      identityConnection('ServiceBus', 'fullyQualifiedNamespace', serviceBus.fqdn, identities['notifications-processor'].clientId),
      {
        Communication__Endpoint: communicationEndpoint
        Communication__EmailSender: emailSenderAddress
        Communication__SmsSender: config.partners.smsSenderNumber
        InvoiceLinks__serviceUri: docsBlobEndpoint
        BlobLinks__PublicBaseUrl: filesBaseUrl
        Notifications__ShopBaseUrl: shopBaseUrl
        Notifications__OperationsEmail: config.alertEmail
      })
  }
  {
    unit: 'readmodels-projections'
    keyPrefix: 'ReadModels:'
    http: false
    maxInstances: config.functions.maximumInstanceCount
    settings: union(
      cosmosSettings(cosmosEndpoint, 'readmodels', identities['readmodels-projections'].clientId),
      identityConnection('ServiceBus', 'fullyQualifiedNamespace', serviceBus.fqdn, identities['readmodels-projections'].clientId),
      { Redis__Endpoint: redisEndpoint })
  }
  {
    unit: 'analytics-processor'
    keyPrefix: 'Analytics:'
    http: true
    // AZURE: the Event Hubs trigger scales up to at most the number of partitions (8); more instances would not speed anything up.
    maxInstances: 8
    settings: union(
      cosmosSettings(cosmosEndpoint, 'analytics', identities['analytics-processor'].clientId),
      identityConnection('ServiceBus', 'fullyQualifiedNamespace', serviceBus.fqdn, identities['analytics-processor'].clientId),
      identityConnection('EventHubs', 'fullyQualifiedNamespace', eventHubsNamespace, identities['analytics-processor'].clientId))
  }
]

module functions '../modules/compute/function-app-flex.bicep' = [for app in functionApps: {
  name: 'func-${app.unit}'
  params: {
    name: resourceName('func', app.unit, config.name, config.regionCode)
    location: location
    tags: tags(config.name, split(app.unit, '-')[0], 'confidential')
    identityId: identities[app.unit].id
    identityClientId: identities[app.unit].clientId
    integrationSubnetId: subnetIds['snet-func']
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    storageAccountName: functionsStorageName
    storageBlobEndpoint: functionsBlobEndpoint
    deploymentContainer: 'deploy-${app.unit}'
    maximumInstanceCount: app.maxInstances
    alwaysReadyHttpInstances: app.http ? config.functions.alwaysReadyInstances : 0
    appSettings: union(
      commonSettings(app.unit, app.keyPrefix, appInsightsConnectionString, appConfigurationEndpoint),
      app.settings,
      runtimeFunctionSettings[?app.unit] ?? {})
    authClientId: app.http ? config.entra.functionAuthClientIds[app.unit] : ''
    authTenantId: config.entra.workforceTenantId
  }
}]

var functionUnits = map(functionApps, app => app.unit)

// Inbound access to the applications only through private endpoints, including functions without HTTP endpoints.
// AZURE: a "sites" private endpoint covers two names: the application and its SCM (Kudu) endpoint, through which the
// pipeline deploys the package. A function with publicNetworkAccess: Disabled and no private endpoint still works
// (Service Bus and Event Hubs triggers are outbound traffic), but code cannot be deployed to it, not even from a runner in the VNet.
module functionEndpoints '../modules/network/private-endpoint.bicep' = [for (app, i) in functionApps: {
  name: 'pe-func-${app.unit}'
  params: {
    name: 'pe-shopflow-func-${app.unit}-${config.name}-${config.regionCode}'
    location: location
    tags: appTags
    subnetId: subnetIds['snet-pe']
    targetResourceId: functions[i].outputs.id
    groupId: 'sites'
    privateDnsZoneId: privateDnsZoneIds['privatelink.azurewebsites.net']
  }
}]

module ordersApiEndpoint '../modules/network/private-endpoint.bicep' = {
  name: 'pe-orders-api'
  params: {
    name: 'pe-shopflow-app-orders-${config.name}-${config.regionCode}'
    location: location
    tags: appTags
    subnetId: subnetIds['snet-pe']
    targetResourceId: ordersApi.outputs.id
    groupId: 'sites'
    privateDnsZoneId: privateDnsZoneIds['privatelink.azurewebsites.net']
  }
}

// AZURE: the slot has its own private endpoint (group "sites-<slot>") and its own names in the privatelink.azurewebsites.net zone.
// The pipeline deploys to staging and runs the smoke test there before the swap, so the runner in the VNet must see it.
// The private endpoint stays with the slot: after the swap production traffic still goes through pe-…-orders.
module ordersApiStagingEndpoint '../modules/network/private-endpoint.bicep' = {
  name: 'pe-orders-api-staging'
  params: {
    name: 'pe-shopflow-app-orders-staging-${config.name}-${config.regionCode}'
    location: location
    tags: appTags
    subnetId: subnetIds['snet-pe']
    targetResourceId: ordersApi.outputs.id
    groupId: 'sites-staging'
    privateDnsZoneId: privateDnsZoneIds['privatelink.azurewebsites.net']
  }
}

@description('Backend addresses for API Management (resolved to private addresses inside the VNet).')
output backends object = {
  ordersApi: 'https://${ordersApi.outputs.defaultHostName}'
  inventoryApi: 'https://${inventoryApi.outputs.fqdn}'
  readModelsApi: 'https://${readModelsApi.outputs.fqdn}'
  sagaFunctions: 'https://${functions[indexOf(functionUnits, 'order-saga')].outputs.defaultHostName}/api'
  paymentsFunctions: 'https://${functions[indexOf(functionUnits, 'payments-processor')].outputs.defaultHostName}/api'
  shippingFunctions: 'https://${functions[indexOf(functionUnits, 'shipping-processor')].outputs.defaultHostName}/api'
  notificationsFunctions: 'https://${functions[indexOf(functionUnits, 'notifications-processor')].outputs.defaultHostName}/api'
  analyticsFunctions: 'https://${functions[indexOf(functionUnits, 'analytics-processor')].outputs.defaultHostName}/api'
}
