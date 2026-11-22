// The shared image registry and the identity the pipeline builds images with (group rg-shopflow-shared-weu).

import { builtInRoles } from '../../shared/roles.bicep'

param location string
param tags object
param registryName string
param githubRepository string

// WHY: one registry for all environments: an image built once from a commit goes through dev, test and prod without being
// rebuilt (build once, deploy many). What reaches production is bit for bit what was tested.
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  tags: tags
  sku: { name: 'Premium' }
  properties: {
    // AZURE: no admin account and no anonymous pull. Push and pull only with an Entra ID token (AcrPush, AcrPull roles).
    adminUserEnabled: false
    anonymousPullEnabled: false
    // WHY: a public endpoint, because a GitHub-hosted runner pushes the images and the Container Apps environments of three
    // VNets pull them. Access is protected by authentication, not by the network.
    publicNetworkAccess: 'Enabled'
    zoneRedundancy: 'Enabled'
    policies: {
      // AZURE: Premium deletes untagged manifests (layers of overwritten images) after 30 days.
      retentionPolicy: { status: 'enabled', days: 30 }
    }
  }
}

resource builder 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-shopflow-github-build'
  location: location
  tags: tags
}

// AZURE: the "ref:refs/heads/main" subject is held by jobs started from the main branch without a GitHub environment.
// A pull request cannot build and push an image to the registry (PR images are only scanned on the runner).
resource builderFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: builder
  name: 'github-main'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepository}:ref:refs/heads/main'
    audiences: ['api://AzureADTokenExchange']
  }
}

// WHY: the build identity can only push images. It deploys nothing, so a leaked token from the build job gives no access
// to the environments.
resource push 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, builder.id, builtInRoles.acrPush)
  scope: registry
  properties: {
    principalId: builder.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', builtInRoles.acrPush)
  }
}

output loginServer string = registry.properties.loginServer
output builderClientId string = builder.properties.clientId
