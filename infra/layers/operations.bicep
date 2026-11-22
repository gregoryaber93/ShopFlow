// Operations layer (in the -core group): alerts, action group, workbook.

import { environmentConfig } from '../shared/types.bicep'
import { tags } from '../shared/naming.bicep'

param config environmentConfig
param logAnalyticsWorkspaceId string
param appInsightsId string
param serviceBusId string
param cosmosAccountId string

module alerts '../modules/operations/alerts.bicep' = {
  name: 'alerts'
  params: {
    envName: config.name
    regionCode: config.regionCode
    tags: tags(config.name, 'platform-operations', 'internal')
    alertEmail: config.alertEmail
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    appInsightsId: appInsightsId
    serviceBusId: serviceBusId
    cosmosAccountId: cosmosAccountId
    // The host from the base address, e.g. https://api.psp.example/ → api.psp.example (the Target column in AppDependencies).
    pspHost: split(config.partners.pspBaseUrl, '/')[2]
  }
}
