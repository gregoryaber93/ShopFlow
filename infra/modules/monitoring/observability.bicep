// Log Analytics and Application Insights (workspace-based).

import { resourceName } from '../../shared/naming.bicep'

param envName string
param regionCode string
param location string
param tags object
param retentionDays int
param dailyQuotaGb int
param samplingPercentage int

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: resourceName('log', '', envName, regionCode)
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: retentionDays
    // WHY: the daily cap in dev protects the budget from a logging loop (e.g. a bug in a retry loop). There is no cap in prod,
    // because losing logs during an incident costs more than the bill.
    workspaceCapping: dailyQuotaGb > 0 ? { dailyQuotaGb: dailyQuotaGb } : null
    features: {
      // AZURE: log access based on resource permissions (a team sees its function's logs without a role on the workspace).
      enableLogAccessUsingOnlyResourcePermissions: true
      disableLocalAuth: true
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: resourceName('appi', '', envName, regionCode)
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    SamplingPercentage: samplingPercentage
    // AZURE: no instrumentation key as a secret. With DisableLocalAuth telemetry is accepted only with an Entra ID token, so
    // every application identity gets the Monitoring Metrics Publisher role on this resource.
    // The connection string stops being a secret: by itself it does not allow sending anything.
    DisableLocalAuth: true
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

output workspaceId string = workspace.id
output appInsightsId string = appInsights.id
output appInsightsConnectionString string = appInsights.properties.ConnectionString
