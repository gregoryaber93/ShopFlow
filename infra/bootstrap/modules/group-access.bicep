// Permissions of a pipeline identity in one resource group.

import { builtInRoles } from '../../shared/roles.bicep'

param principalId string

@description('Roles granted directly (e.g. Contributor, Reader).')
param roleDefinitionIds array

@description('Roles the identity may grant to others. Empty list: no RBAC Administrator role.')
param delegableRoleIds array = []

resource assignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for roleId in roleDefinitionIds: {
  name: guid(resourceGroup().id, principalId, roleId)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
  }
}]

var delegableList = join(delegableRoleIds, ', ')

// AZURE: Contributor cannot grant roles, and Bicep creates role assignments for application identities. Owner or
// User Access Administrator would give the pipeline power over all of RBAC (including granting itself Owner). The
// "Role Based Access Control Administrator" role with a condition (constrained delegation, ABAC) allows creating and
// deleting only assignments of the listed roles. A compromised pipeline cannot elevate its own permissions.
resource rbacAdministrator 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(delegableRoleIds)) {
  name: guid(resourceGroup().id, principalId, builtInRoles.roleBasedAccessControlAdministrator)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', builtInRoles.roleBasedAccessControlAdministrator)
    conditionVersion: '2.0'
    condition: '((!(ActionMatches{\'Microsoft.Authorization/roleAssignments/write\'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${delegableList}})) AND ((!(ActionMatches{\'Microsoft.Authorization/roleAssignments/delete\'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${delegableList}}))'
  }
}
