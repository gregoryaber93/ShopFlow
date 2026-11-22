// Container Apps Job started manually from the pipeline, e.g. the EF Core migration bundle for the database in the VNet.

param name string
param location string
param tags object
param environmentId string
param identityId string
param identityClientId string
param image string
param registryServer string
param environmentVariables object

@description('Container command and arguments (e.g. the path to the migration bundle).')
param command array

resource job 'Microsoft.App/jobs@2024-10-02-preview' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      // WHY: a manual trigger (az containerapp job start) from a pipeline step before the applications are deployed.
      // The SQL database has no public endpoint, so migrations must run inside the VNet.
      triggerType: 'Manual'
      replicaTimeout: 1800
      // A migration that failed is not retried automatically: a partially applied script needs a human.
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        {
          server: registryServer
          identity: identityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'job'
          image: image
          command: command
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat(
            [{ name: 'AZURE_CLIENT_ID', value: identityClientId }],
            map(items(environmentVariables), variable => { name: variable.key, value: variable.value }))
        }
      ]
    }
  }
}

output id string = job.id
