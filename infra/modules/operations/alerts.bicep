// Alerts and the operations workbook. Every alert has a matching runbook in docs/runbooks.

import { resourceName } from '../../shared/naming.bicep'

param envName string
param regionCode string
param tags object
param alertEmail string
param logAnalyticsWorkspaceId string
param appInsightsId string
param serviceBusId string
param cosmosAccountId string

@description('Payment provider API host (e.g. api.psp.example), used to narrow the payment-provider-errors alert.')
param pspHost string

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: resourceName('ag', 'oncall', envName, regionCode)
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'sf-${envName}'
    enabled: true
    emailReceivers: [
      {
        name: 'oncall'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

// WHY: an alert on every message in a DLQ, split by entity. A DLQ does not grow "normally": every message is an order that
// nobody handled. Runbook: docs/runbooks/dlq-growth.md, tool: tools/ShopFlow.DlqReplay.
resource deadLetters 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: resourceName('alert', 'sb-deadletters', envName, regionCode)
  location: 'global'
  tags: tags
  properties: {
    description: 'Messages in a Service Bus DLQ (per queue or topic). Runbook: dlq-growth.'
    severity: 2
    enabled: true
    scopes: [serviceBusId]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'deadlettered'
          criterionType: 'StaticThresholdCriterion'
          metricName: 'DeadletteredMessages'
          metricNamespace: 'Microsoft.ServiceBus/namespaces'
          timeAggregation: 'Maximum'
          operator: 'GreaterThan'
          threshold: 0
          dimensions: [{ name: 'EntityName', operator: 'Include', values: ['*'] }]
        }
      ]
    }
    actions: [{ actionGroupId: actionGroup.id }]
  }
}

resource backlog 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: resourceName('alert', 'sb-backlog', envName, regionCode)
  location: 'global'
  tags: tags
  properties: {
    description: 'The queue backlog keeps growing for 15 minutes: the consumer cannot keep up or has stopped.'
    severity: 3
    enabled: true
    scopes: [serviceBusId]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'active'
          criterionType: 'StaticThresholdCriterion'
          metricName: 'ActiveMessages'
          metricNamespace: 'Microsoft.ServiceBus/namespaces'
          timeAggregation: 'Minimum'
          operator: 'GreaterThan'
          threshold: 1000
          dimensions: [{ name: 'EntityName', operator: 'Include', values: ['*'] }]
        }
      ]
    }
    actions: [{ actionGroupId: actionGroup.id }]
  }
}

// AZURE: single 429s in Cosmos DB are normal (the SDK retries according to x-ms-retry-after-ms). The alarm is their sustained
// number: then the SDK gives up and exceptions reach the application. Runbook: cosmos-throttling.
resource cosmosThrottling 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: resourceName('alert', 'cosmos-429', envName, regionCode)
  location: 'global'
  tags: tags
  properties: {
    description: 'Cosmos DB rejects requests with 429 (RU/s exceeded). Runbook: cosmos-throttling.'
    severity: 2
    enabled: true
    scopes: [cosmosAccountId]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'throttled'
          criterionType: 'StaticThresholdCriterion'
          metricName: 'TotalRequests'
          metricNamespace: 'Microsoft.DocumentDB/databaseAccounts'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 500
          dimensions: [
            { name: 'StatusCode', operator: 'Include', values: ['429'] }
            { name: 'CollectionName', operator: 'Include', values: ['*'] }
          ]
        }
      ]
    }
    actions: [{ actionGroupId: actionGroup.id }]
  }
}

// Alerts from KQL queries over Application Insights data (OpenTelemetry → App* tables).
var logAlerts = [
  {
    name: 'failed-requests'
    description: 'The failure rate per service exceeds 5% (HTTP and functions).'
    severity: 2
    query: 'AppRequests | where TimeGenerated > ago(10m) | summarize total = count(), failed = countif(Success == false) by AppRoleName | where total > 20 and failed * 100.0 / total > 5'
  }
  {
    name: 'projection-lag'
    description: 'Customers see stale orders: the 95th percentile of the projection lag is above 60 s.'
    severity: 3
    query: 'AppMetrics | where TimeGenerated > ago(15m) and Name == "shopflow.readmodels.projection_lag" | summarize p95 = percentile(Max, 95) | where p95 > 60'
  }
  {
    name: 'outbox-lag'
    description: 'Events wait in the outbox for more than 2 minutes: the relay has stopped or has no Service Bus permissions.'
    severity: 2
    query: 'AppMetrics | where TimeGenerated > ago(15m) and Name == "shopflow.outbox.lag" | summarize worst = max(Max) by AppRoleName | where worst > 120'
  }
  {
    name: 'payment-provider-errors'
    description: 'Calls to the payment provider are failing. Runbook: payment-failure (section A).'
    severity: 1
    // AZURE: in Log Analytics tables the Type column is the table name ("AppDependencies"), and the dependency kind is in
    // DependencyType. Filtering on the PSP host leaves out the same function's Cosmos, Service Bus and Key Vault calls.
    query: 'AppDependencies | where TimeGenerated > ago(10m) and AppRoleName == "payments-processor" and Target has "${pspHost}" | summarize total = count(), failed = countif(Success == false) | where total > 10 and failed * 100.0 / total > 20'
  }
  // WHY: two alerts on Critical log entries, not on metrics. Both events are rare and each needs a human: the saga waits
  // without a time limit for an operator decision, and a skipped outbox document is an event nobody received.
  // SeverityLevel 4 = Critical in the AppTraces table.
  {
    name: 'saga-manual-intervention'
    description: 'An order saga is waiting for an operator decision (RequiresManualIntervention). Runbook: payment-failure (section B).'
    severity: 1
    query: 'AppTraces | where TimeGenerated > ago(10m) and SeverityLevel == 4 and Message has "requires manual intervention"'
  }
  {
    name: 'outbox-malformed'
    description: 'The relay skipped a malformed outbox document; the event was NOT published. Runbook: dlq-growth.'
    severity: 1
    query: 'AppTraces | where TimeGenerated > ago(10m) and SeverityLevel == 4 and Message has "is malformed and was skipped"'
  }
]

resource scheduledAlerts 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = [for alert in logAlerts: {
  name: resourceName('alert', alert.name, envName, regionCode)
  location: resourceGroup().location
  tags: tags
  properties: {
    displayName: alert.name
    description: alert.description
    severity: alert.severity
    enabled: true
    scopes: [logAnalyticsWorkspaceId]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      allOf: [
        {
          query: alert.query
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    autoMitigate: true
    actions: { actionGroups: [actionGroup.id] }
  }
}]

resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  name: guid(resourceGroup().id, 'shopflow-operations')
  location: resourceGroup().location
  tags: tags
  kind: 'shared'
  properties: {
    displayName: 'ShopFlow ${envName}: operacje'
    category: 'workbook'
    sourceId: appInsightsId
    serializedData: loadTextContent('../../workbooks/shopflow-operations.workbook.json')
  }
}
