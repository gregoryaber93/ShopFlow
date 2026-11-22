// Identifiers of Azure built-in roles (constant across all tenants).
// WHY: GUIDs in one place with the name next to them. A role assigned with a typo in the GUID does not exist, and the
// deployment fails with RoleDefinitionDoesNotExist only in Azure.

@export()
var builtInRoles = {
  reader: 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
  contributor: 'b24988ac-6180-42a0-ab88-20f7382dd24c'
  roleBasedAccessControlAdministrator: 'f58310d9-a9f6-439a-9e8d-f62e7b41a168'
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  acrPush: '8311e382-0749-4cb8-b61a-304f252e45ec'
  monitoringMetricsPublisher: '3913510d-42f4-4e42-8a64-420c390055eb'

  serviceBusDataOwner: '090c5cfd-751d-490a-894a-3ce6f1109419'
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
  eventHubsDataSender: '2b629674-e913-4c01-ae53-ef4638d8f975'
  eventHubsDataReceiver: 'a638d3c7-ab3a-418d-83e6-5f17a39d4fde'

  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  storageBlobDataReader: '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
  storageBlobDelegator: 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  appConfigurationDataReader: '516239f1-63e1-4d78-a4de-a74fb236a071'

  durableTaskDataContributor: '0ad04412-c4d5-4796-b79c-f76d14c8d402'
  communicationAndEmailServiceOwner: '09976791-48a7-449e-bb21-39d1a415f350'
}

@export()
@description('Cosmos DB data-plane roles (sqlRoleDefinitions), a separate RBAC system from Azure RBAC.')
var cosmosDataRoles = {
  dataReader: '00000000-0000-0000-0000-000000000001'
  dataContributor: '00000000-0000-0000-0000-000000000002'
}

@export()
@description('Deployable units with their own managed identity (names as in MessagingTopology.Consumers).')
var deployableUnits = [
  'orders-api'
  'orders-processors'
  'order-saga'
  'inventory-api'
  'inventory-worker'
  'inventory-migrations'
  'payments-processor'
  'shipping-processor'
  'notifications-processor'
  'readmodels-projections'
  'readmodels-api'
  'analytics-processor'
]

@export()
@description('Roles the pipeline may grant to application identities. Nothing outside this list (ABAC condition in the bootstrap).')
var delegableRoles = [
  builtInRoles.serviceBusDataOwner
  builtInRoles.serviceBusDataSender
  builtInRoles.serviceBusDataReceiver
  builtInRoles.eventHubsDataSender
  builtInRoles.eventHubsDataReceiver
  builtInRoles.storageBlobDataOwner
  builtInRoles.storageBlobDataContributor
  builtInRoles.storageBlobDataReader
  builtInRoles.storageBlobDelegator
  builtInRoles.storageQueueDataContributor
  builtInRoles.storageTableDataContributor
  builtInRoles.keyVaultSecretsUser
  builtInRoles.appConfigurationDataReader
  builtInRoles.monitoringMetricsPublisher
  builtInRoles.durableTaskDataContributor
  builtInRoles.communicationAndEmailServiceOwner
  builtInRoles.acrPull
]

@export()
@description('Keys of the custom roles from infra/bootstrap/shared.bicep. Role definition name = guid(subscription().id, key).')
var customRoleKeys = {
  deploymentOperator: 'shopflow-deployment-operator'
  whatIfReader: 'shopflow-what-if-reader'
}
