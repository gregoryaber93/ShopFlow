// The AcrPull role on the shared registry (group rg-shopflow-shared-weu), deployed in that group's scope.
// WHY: a role assignment must be deployed in the scope of the resource it concerns. The registry is shared by all
// environments (build once, deploy many), so this module is called with scope: resourceGroup(shared).

import { builtInRoles } from '../../shared/roles.bicep'

param registryName string
param principalIds array

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in principalIds: {
  name: guid(registry.id, principalId, builtInRoles.acrPull)
  scope: registry
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', builtInRoles.acrPull)
  }
}]

output loginServer string = registry.properties.loginServer
