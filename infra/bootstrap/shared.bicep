// Bootstrap shared by all environments. Deployed by an administrator (subscription Owner), once and on changes:
//   az deployment sub create --location westeurope --template-file infra/bootstrap/shared.bicep --parameters infra/bootstrap/shared.bicepparam
//
// Creates: the rg-shopflow-shared-weu group, the image registry with the build identity (OIDC from the main branch)
// and two custom roles for the pipeline identities.

targetScope = 'subscription'

import { tags } from '../shared/naming.bicep'
import { customRoleKeys } from '../shared/roles.bicep'

param location string
param registryName string
param githubRepository string

resource sharedGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-shopflow-shared-weu'
  location: location
  tags: tags('shared', 'platform-shared', 'internal')
}

module registry 'modules/registry.bicep' = {
  name: 'shared-registry'
  scope: sharedGroup
  params: {
    location: location
    tags: tags('shared', 'platform-shared', 'internal')
    registryName: registryName
    githubRepository: githubRepository
  }
}

// AZURE: a subscription-scope deployment (az deployment sub create) writes a deployment object on the subscription, and
// modules create deployment objects in resource groups. Contributor granted on the groups does not cover that.
// WHY: this role grants rights only to deployment objects (Microsoft.Resources/deployments). Azure Resource Manager checks
// permissions for every resource in the template anyway, so a dev pipeline with this role could start a deployment in the
// prod group, but every resource write would be denied. It can change things only where it has Contributor.
resource deploymentOperator 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, customRoleKeys.deploymentOperator)
  properties: {
    roleName: 'ShopFlow Deployment Operator'
    description: 'Create, validate and what-if ARM deployments in the subscription. No rights to resources.'
    type: 'CustomRole'
    assignableScopes: [subscription().id]
    permissions: [
      {
        actions: [
          'Microsoft.Resources/deployments/*'
          'Microsoft.Resources/subscriptions/resourceGroups/read'
        ]
        notActions: []
      }
    ]
  }
}

// WHY: what-if in a pull request needs read access to the state (Reader on the dev groups) plus the validate and what-if
// actions. Without Microsoft.Resources/deployments/write the identity cannot create a deployment, not even an empty one.
resource whatIfReader 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, customRoleKeys.whatIfReader)
  properties: {
    roleName: 'ShopFlow What-If Reader'
    description: 'Validate and what-if ARM deployments in the subscription, without creating deployments.'
    type: 'CustomRole'
    assignableScopes: [subscription().id]
    permissions: [
      {
        actions: [
          'Microsoft.Resources/deployments/read'
          'Microsoft.Resources/deployments/validate/action'
          'Microsoft.Resources/deployments/whatIf/action'
          'Microsoft.Resources/subscriptions/resourceGroups/read'
        ]
        notActions: []
      }
    ]
  }
}

output registryLoginServer string = registry.outputs.loginServer
@description('The AZURE_BUILD_CLIENT_ID repository variable in GitHub.')
output buildClientId string = registry.outputs.builderClientId
