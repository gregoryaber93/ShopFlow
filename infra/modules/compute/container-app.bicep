// An application in Container Apps: image pulled from ACR with an identity, probes, scale rules (HTTP or KEDA).

param name string
param location string
param tags object
param environmentId string
param identityId string
param identityClientId string
param image string
param registryServer string

@description('Ingress inside the VNet; false = no ingress (worker).')
param ingressEnabled bool

param minReplicas int
param maxReplicas int

@description('Environment variables (name → value).')
param environmentVariables object

@description('Scale rules in the Container Apps format (scale.rules).')
param scaleRules array

param cpu string = '0.5'
param memory string = '1Gi'

// API 2024-10-02-preview: the "identity" field in KEDA scale rules (the scaler signs in with the application identity).
resource app 'Microsoft.App/containerApps@2024-10-02-preview' = {
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
      // AZURE: Single mode: one active revision. The pipeline creates a new revision with a new image (immutable SHA tag)
      // and Container Apps switches traffic once the new replicas pass the readiness probe.
      activeRevisionsMode: 'Single'
      ingress: ingressEnabled
        ? {
            // In an internal environment "external" means "reachable from the VNet", not from the internet.
            external: true
            targetPort: 8080
            transport: 'auto'
            allowInsecure: false
          }
        : null
      // AZURE: the image is pulled from ACR with a managed identity (AcrPull role), without an admin account or a password in configuration.
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
          name: 'app'
          image: image
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: concat(
            [{ name: 'AZURE_CLIENT_ID', value: identityClientId }],
            map(items(environmentVariables), variable => { name: variable.key, value: variable.value }))
          probes: ingressEnabled
            ? [
                {
                  type: 'Startup'
                  httpGet: { path: '/health/live', port: 8080 }
                  periodSeconds: 5
                  failureThreshold: 24
                }
                {
                  type: 'Liveness'
                  httpGet: { path: '/health/live', port: 8080 }
                  periodSeconds: 15
                }
                {
                  // WHY: readiness checks dependencies (database, cache). A replica without database access drops out of traffic
                  // but is not restarted (a restart would not fix a database outage and would multiply cold starts).
                  type: 'Readiness'
                  httpGet: { path: '/health/ready', port: 8080 }
                  periodSeconds: 10
                }
              ]
            : []
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: scaleRules
      }
    }
  }
}

output id string = app.id
output fqdn string = ingressEnabled ? app.properties.configuration.ingress.fqdn : ''
