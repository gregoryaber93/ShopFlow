// Private networking for GitHub-hosted runners (GitHub Enterprise Cloud, "Azure private networking").
//
// AZURE: the GitHub.Network/networkSettings resource links a subnet to a GitHub organization. When a job lands in a runner
// group with this network configuration, GitHub creates the runner machine with a network interface in our subnet. The
// runner resolves names through the private DNS zones linked to the VNet, so it sees the App Service and Functions SCM
// endpoint (private endpoint only), and outbound traffic to GitHub and Azure goes through the NAT Gateway (a fixed IP).
// WHY: the alternative is self-hosted runners, e.g. Container Apps Jobs with the KEDA "github-runner" scaler. They give the
// same without GitHub Enterprise, but the team maintains the runner image, its updates and the GitHub App key.
// Here the machines are ephemeral and maintained by GitHub, and we only provide the subnet.
//
// After deployment: the GitHubId tag value (the githubId output) is entered in the GitHub organization settings
// (Hosted compute networking → new network configuration), and the configuration is assigned to the runner group
// "shopflow-<env>". Workflows select it with runs-on: { group: shopflow-<env> }.
// Requires the GitHub.Network resource provider to be registered in the subscription (bootstrap).

import { resourceName } from '../../shared/naming.bicep'

param envName string
param regionCode string
param location string
param tags object

@description('Subnet delegated to GitHub.Network/networkSettings.')
param subnetId string

@description('databaseId of the GitHub organization (or enterprise): gh api graphql -f query=\'{ organization(login: "shopflow-org") { databaseId } }\'.')
param businessId string

resource networkSettings 'GitHub.Network/networkSettings@2024-04-02' = {
  name: resourceName('ghns', 'runners', envName, regionCode)
  location: location
  tags: tags
  properties: {
    subnetId: subnetId
    businessId: businessId
  }
}

// AZURE: the resource provider writes the identifier for GitHub into the tags (GitHubId) after the resource is created.
// WHY: the "existing" reference reads the resource state in Azure. The tags property of the declared resource would mean our
// own tags from the template, without GitHubId. Outputs are evaluated after resources are deployed, so the resource exists.
resource deployed 'GitHub.Network/networkSettings@2024-04-02' existing = {
  name: networkSettings.name
}

output githubId string = deployed.tags.GitHubId
