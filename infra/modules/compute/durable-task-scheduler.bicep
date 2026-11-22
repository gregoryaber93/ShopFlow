// Durable Task Scheduler: the managed Durable Functions state backend for the order saga.

import { resourceName } from '../../shared/naming.bicep'
import { builtInRoles } from '../../shared/roles.bicep'

param envName string
param regionCode string
param location string
param tags object
param sagaPrincipalId string

@description('NAT Gateway: the only address the functions connect to the scheduler endpoint from.')
param allowedEgressIp string

// AZURE: DTS instead of Azure Storage as the Durable Functions backend. Orchestration history, queues and timers live
// in a managed service with its own dashboard, without tables and queues in a Storage account, which become a bottleneck
// under heavy load. Access only with an identity (connection string: Authentication=ManagedIdentity).
resource scheduler 'Microsoft.DurableTask/schedulers@2025-04-01-preview' = {
  name: resourceName('dts', 'saga', envName, regionCode)
  location: location
  tags: tags
  properties: {
    sku: {
      name: envName == 'prod' ? 'Dedicated' : 'Consumption'
      capacity: envName == 'prod' ? 1 : null
    }
    // WHY: no private endpoint in this project (see docs/architecture/network.md, network exceptions).
    // The endpoint accepts traffic only from the NAT Gateway address and only with an identity token that has a role on the task hub.
    ipAllowlist: ['${allowedEgressIp}/32']
  }
}

resource taskHub 'Microsoft.DurableTask/schedulers/taskHubs@2025-04-01-preview' = {
  parent: scheduler
  name: 'order-saga'
}

resource sagaAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(taskHub.id, sagaPrincipalId, builtInRoles.durableTaskDataContributor)
  scope: taskHub
  properties: {
    principalId: sagaPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', builtInRoles.durableTaskDataContributor)
  }
}

output endpoint string = scheduler.properties.endpoint
output taskHubName string = taskHub.name
