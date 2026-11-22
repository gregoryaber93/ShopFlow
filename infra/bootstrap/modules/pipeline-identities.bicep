// GitHub Actions identities of one environment (core group). Sign-in through OIDC, no secret in GitHub.

import { resourceName } from '../../shared/naming.bicep'

param envName string
param regionCode string
param location string
param tags object

@description('GitHub repository (organization/name).')
param githubRepository string

@description('Read-only identity for what-if in pull requests (dev only).')
param createPlanIdentity bool

var issuer = 'https://token.actions.githubusercontent.com'

resource deployer 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: resourceName('id', 'github-deploy', envName, regionCode)
  location: location
  tags: tags
}

// AZURE: the federated credential trusts OIDC tokens issued by GitHub for a specific subject (the sub claim).
// The "environment:prod" subject is held only by jobs running in the GitHub "prod" environment, i.e. after approval by
// the required reviewers and only from the branches allowed in the environment. A branch, a fork or another workflow
// cannot get a token for this identity.
resource deployerFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: deployer
  name: 'github-${envName}'
  properties: {
    issuer: issuer
    subject: 'repo:${githubRepository}:environment:${envName}'
    audiences: ['api://AzureADTokenExchange']
  }
}

resource planner 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (createPlanIdentity) {
  name: resourceName('id', 'github-plan', envName, regionCode)
  location: location
  tags: tags
}

// WHY: what-if in a pull request runs code from an unmerged branch. It gets a separate identity with read-only access and
// what-if operations. A malicious workflow change in a PR deploys nothing, because this identity has no role with write
// access. GitHub does not issue OIDC tokens for pull requests from forks.
resource plannerFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = if (createPlanIdentity) {
  parent: planner
  name: 'github-pull-request'
  properties: {
    issuer: issuer
    subject: 'repo:${githubRepository}:pull_request'
    audiences: ['api://AzureADTokenExchange']
  }
}

output deployerPrincipalId string = deployer.properties.principalId
output deployerClientId string = deployer.properties.clientId
output plannerPrincipalId string = createPlanIdentity ? planner!.properties.principalId : ''
output plannerClientId string = createPlanIdentity ? planner!.properties.clientId : ''
