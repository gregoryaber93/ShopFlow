// Bootstrap of one environment. Deployed by an administrator (subscription Owner) after shared.bicep, once and on changes:
//   az deployment sub create --location westeurope --template-file infra/bootstrap/environment.bicep --parameters infra/bootstrap/dev.bicepparam
//
// Creates what the pipeline deliberately cannot do itself: resource groups, the data group lock, the GitHub Actions
// identities with their permissions, and the budget. Everything else is deployed by the pipeline from infra/main.bicep.
// WHY: if main.bicep granted permissions to the identity that deploys it, every pipeline deployment would need the right to
// grant the Contributor and RBAC Administrator roles, i.e. in practice full control over RBAC.

targetScope = 'subscription'

import { environmentName } from '../shared/types.bicep'
import { tags } from '../shared/naming.bicep'
import { builtInRoles, customRoleKeys, delegableRoles } from '../shared/roles.bicep'

param envName environmentName
param location string
param regionCode string
param githubRepository string

@description('Resource group of the shared image registry (shared.bicep).')
param sharedGroupName string = 'rg-shopflow-shared-weu'

@description('Monthly budget of the environment in the billing currency; alerts at 80% and at a 100% forecast.')
param monthlyBudget int
param alertEmail string

@description('First day of the current month (a budget cannot start earlier).')
param budgetStartDate string = '${utcNow('yyyy-MM')}-01'

var groups = {
  core: { name: 'rg-shopflow-${envName}-${regionCode}-core', tags: tags(envName, 'platform-core', 'internal') }
  data: { name: 'rg-shopflow-${envName}-${regionCode}-data', tags: tags(envName, 'platform-data', 'confidential') }
  apps: { name: 'rg-shopflow-${envName}-${regionCode}-apps', tags: tags(envName, 'platform-apps', 'confidential') }
}

resource resourceGroups 'Microsoft.Resources/resourceGroups@2024-03-01' = [for group in items(groups): {
  name: group.value.name
  location: location
  tags: group.value.tags
}]

// AZURE: a CanNotDelete lock on the data group: nobody (Owner included) can delete a database, the Cosmos account or the
// Service Bus namespace without removing the lock first. The pipeline with the Contributor role has no
// Microsoft.Authorization/locks permission, so only an administrator places and removes the lock.
module dataLock 'modules/delete-lock.bicep' = {
  name: 'lock-data-${envName}'
  scope: resourceGroup(groups.data.name)
  params: {
    notes: 'ShopFlow data (${envName}). Removing the lock requires a change request.'
  }
  dependsOn: [resourceGroups]
}

module identities 'modules/pipeline-identities.bicep' = {
  name: 'pipeline-identities-${envName}'
  scope: resourceGroup(groups.core.name)
  params: {
    envName: envName
    regionCode: regionCode
    location: location
    tags: groups.core.tags
    githubRepository: githubRepository
    createPlanIdentity: envName == 'dev'
  }
  dependsOn: [resourceGroups]
}

// Pipeline: Contributor and a constrained RBAC Administrator on the three groups of the environment.
module deployerGroupAccess 'modules/group-access.bicep' = [for group in items(groups): {
  name: 'deployer-access-${group.key}-${envName}'
  scope: resourceGroup(group.value.name)
  params: {
    principalId: identities.outputs.deployerPrincipalId
    roleDefinitionIds: [builtInRoles.contributor]
    delegableRoleIds: delegableRoles
  }
  dependsOn: [resourceGroups]
}]

// The environment pipeline grants application identities AcrPull on the shared registry (acr-pull module). Only that role.
module deployerRegistryAccess 'modules/group-access.bicep' = {
  name: 'deployer-access-shared-${envName}'
  scope: resourceGroup(sharedGroupName)
  params: {
    principalId: identities.outputs.deployerPrincipalId
    roleDefinitionIds: []
    delegableRoleIds: [builtInRoles.acrPull]
  }
}

resource deploymentOperator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, envName, customRoleKeys.deploymentOperator)
  properties: {
    principalId: identities.outputs.deployerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', guid(subscription().id, customRoleKeys.deploymentOperator))
  }
}

// What-if in pull requests (dev only): read access to the dev groups and the registry, plus the what-if role on the subscription.
var planReadGroups = envName == 'dev' ? concat(map(items(groups), group => group.value.name), [sharedGroupName]) : []

module plannerGroupAccess 'modules/group-access.bicep' = [for groupName in planReadGroups: {
  name: 'planner-access-${groupName}'
  scope: resourceGroup(groupName)
  params: {
    principalId: identities.outputs.plannerPrincipalId
    roleDefinitionIds: [builtInRoles.reader]
  }
  dependsOn: [resourceGroups]
}]

resource plannerWhatIf 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (envName == 'dev') {
  name: guid(subscription().id, envName, customRoleKeys.whatIfReader)
  properties: {
    principalId: identities.outputs.plannerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', guid(subscription().id, customRoleKeys.whatIfReader))
  }
}

// AZURE: a budget does not block spending, it only notifies. The forecast alert (Forecasted) arrives before the money is
// spent, when the month's trend points to an overrun. The tag filter counts only this environment's resources
// (tags are mandatory and enforced by Azure Policy).
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: 'budget-shopflow-${envName}'
  properties: {
    category: 'Cost'
    amount: monthlyBudget
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate }
    filter: {
      and: [
        { tags: { name: 'workload', operator: 'In', values: ['shopflow'] } }
        { tags: { name: 'environment', operator: 'In', values: [envName] } }
      ]
    }
    notifications: {
      actualOver80: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: [alertEmail]
      }
      forecastOver100: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: [alertEmail]
      }
    }
  }
}

@description('The AZURE_CLIENT_ID variable of the GitHub environment.')
output deployerClientId string = identities.outputs.deployerClientId

@description('The AZURE_PLAN_CLIENT_ID repository variable (dev only).')
output plannerClientId string = identities.outputs.plannerClientId
