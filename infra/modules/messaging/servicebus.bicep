// Service Bus Premium with the topology loaded from messaging-topology.json (the tests and shopflow-dlq read the same source).

import { resourceName } from '../../shared/naming.bicep'
import { builtInRoles } from '../../shared/roles.bicep'

param envName string
param regionCode string
param location string
param tags object
param capacity int
param zoneRedundant bool
param logAnalyticsWorkspaceId string

@description('Map: deployable unit → principalId (names as in consumer/publisher/senders in the topology).')
param principalIds object

@description('Receivers that must read queue metrics (KEDA scaling): [{ queue, unit }].')
param scalerQueueReaders array

@description('Identity of the Event Grid system topic that sends BlobCreated events to invoice-blob-events.')
param eventGridPrincipalId string

var topology = loadJsonContent('messaging-topology.json')
var replayProperty = topology.replay.property
var queueDefinitions = topology.serviceBus.queues
var topicDefinitions = topology.serviceBus.topics
var queueNames = map(queueDefinitions, queue => queue.name)
var topicNames = map(topicDefinitions, topic => topic.name)

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: resourceName('sbns', '', envName, regionCode)
  location: location
  tags: tags
  // AZURE: Premium, because it gives isolated resources (predictable latency), private endpoints, zone redundancy
  // and messages up to 100 MB. Standard supports neither private endpoints nor VNets.
  sku: {
    name: 'Premium'
    tier: 'Premium'
    capacity: capacity
  }
  properties: {
    // AZURE: SAS keys disabled; applications and Event Grid connect with an Entra ID token and a role on a specific entity.
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    zoneRedundant: zoneRedundant
    premiumMessagingPartitions: 1
    // See networkRuleSets: a public endpoint with a default deny, open only to trusted services (Event Grid).
    publicNetworkAccess: 'Enabled'
  }
}

// AZURE: with publicNetworkAccess = Disabled even Event Grid (a trusted Microsoft service) cannot deliver events, because it
// runs outside our VNet. A default deny without IP rules plus the trusted services exception has the same effect for the
// internet, but admits the system topic authenticated with a managed identity.
resource networkRules 'Microsoft.ServiceBus/namespaces/networkRuleSets@2022-10-01-preview' = {
  parent: namespace
  name: 'default'
  properties: {
    publicNetworkAccess: 'Enabled'
    defaultAction: 'Deny'
    trustedServiceAccessEnabled: true
    ipRules: []
    virtualNetworkRules: []
  }
}

resource queues 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = [for queue in queueDefinitions: {
  parent: namespace
  name: queue.name
  properties: {
    // AZURE: requiresSession and requiresDuplicateDetection cannot be changed after a queue is created. A change in the topology
    // requires a new queue and a traffic migration, which is why the decision about sessions is made at design time.
    requiresSession: queue.requiresSession
    requiresDuplicateDetection: queue.duplicateDetectionWindow != null
    duplicateDetectionHistoryTimeWindow: queue.duplicateDetectionWindow ?? 'PT10M'
    maxDeliveryCount: queue.maxDeliveryCount
    lockDuration: queue.lockDuration
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
    maxSizeInMegabytes: 5120
  }
}]

resource topics 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = [for topic in topicDefinitions: {
  parent: namespace
  name: topic.name
  properties: {
    requiresDuplicateDetection: topic.duplicateDetectionWindow != null
    duplicateDetectionHistoryTimeWindow: topic.duplicateDetectionWindow ?? 'PT10M'
    defaultMessageTimeToLive: 'P14D'
    maxSizeInMegabytes: 5120
  }
}]

// Subscriptions flattened into one list, with the data needed to build the rules.
var subscriptionDefinitions = flatten(map(topicDefinitions, topic => map(topic.subscriptions, subscription => {
  topic: topic.name
  name: subscription.name
  forwardTo: subscription.forwardTo
  consumer: subscription.?consumer
  maxDeliveryCount: subscription.maxDeliveryCount
  // A topic with a directly read subscription needs "ReplayTarget IS NULL" rules (see tools/ShopFlow.DlqReplay).
  replayAware: length(filter(topic.subscriptions, other => other.forwardTo == null)) > 0
  correlation: subscription.filter != null && subscription.filter.?correlation != null
  subjects: subscription.filter == null ? [] : (subscription.filter.?subjects ?? [subscription.filter.correlation.subject])
  properties: subscription.filter == null ? {} : (subscription.filter.?correlation.?properties ?? {})
})))

var ruleDefinitions = map(subscriptionDefinitions, subscription => union(subscription, {
  // WHY: a correlation filter only where an equality match is enough (it is cheaper for the broker to evaluate than SQL).
  // The "IS NULL" condition already requires a SQL rule.
  useCorrelation: subscription.correlation && !subscription.replayAware
  clauses: concat(
    empty(subscription.subjects)
      ? []
      : [length(subscription.subjects) == 1 ? 'sys.Label = \'${subscription.subjects[0]}\'' : 'sys.Label IN (${join(map(subscription.subjects, subject => '\'${subject}\''), ', ')})'],
    map(items(subscription.properties), property => '${property.key} = \'${property.value}\''),
    subscription.replayAware ? ['${replayProperty} IS NULL'] : [])
}))

resource subscriptions 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = [for subscription in subscriptionDefinitions: {
  parent: topics[indexOf(topicNames, subscription.topic)]
  name: subscription.name
  properties: {
    maxDeliveryCount: subscription.maxDeliveryCount
    lockDuration: 'PT1M'
    deadLetteringOnMessageExpiration: true
    // An exception while evaluating a filter (e.g. a wrong property type) moves the message to the DLQ instead of losing it.
    deadLetteringOnFilterEvaluationExceptions: true
    // AZURE: auto-forward moves the message to the consumer's queue inside the broker, with no code and no separate role.
    forwardTo: subscription.forwardTo
  }
  dependsOn: [queues]
}]

// AZURE: every subscription gets a $Default rule with a "let everything through" filter when it is created. A rule with the
// same name replaces it with our filter; adding a rule with a different name would keep $Default and the filter would achieve nothing.
resource defaultRules 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = [for (rule, i) in ruleDefinitions: {
  parent: subscriptions[i]
  name: '$Default'
  properties: rule.useCorrelation
    ? {
        filterType: 'CorrelationFilter'
        correlationFilter: {
          label: rule.subjects[0]
          properties: empty(rule.properties) ? null : rule.properties
        }
      }
    : {
        filterType: 'SqlFilter'
        sqlFilter: { sqlExpression: empty(rule.clauses) ? '1=1' : join(rule.clauses, ' AND ') }
      }
}]

var directReadIndexes = filter(range(0, length(subscriptionDefinitions)), i => subscriptionDefinitions[i].forwardTo == null)

// Second rule of a directly read subscription: lets through a message replayed from its DLQ (ReplayTarget = name).
resource replayRules 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = [for i in directReadIndexes: {
  parent: subscriptions[i]
  name: 'replay'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: { sqlExpression: '${replayProperty} = \'${subscriptionDefinitions[i].name}\'' }
  }
  dependsOn: [defaultRules]
}]

// --- RBAC per entity -----------------------------------------------------------------------------------------------
// WHY: roles on a queue, topic or subscription, never on the namespace. orders-processors can send only to
// orders-events; a compromised identity cannot publish a fake PaymentCaptured.

var senderRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', builtInRoles.serviceBusDataSender)
var receiverRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', builtInRoles.serviceBusDataReceiver)
var ownerRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', builtInRoles.serviceBusDataOwner)

resource topicPublishers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (topic, i) in topicDefinitions: {
  name: guid(namespace.id, topic.name, principalIds[topic.publisher], senderRole)
  scope: topics[i]
  properties: {
    principalId: principalIds[topic.publisher]
    principalType: 'ServicePrincipal'
    roleDefinitionId: senderRole
  }
}]

resource queueConsumers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (queue, i) in queueDefinitions: {
  name: guid(namespace.id, queue.name, principalIds[queue.consumer], receiverRole)
  scope: queues[i]
  properties: {
    principalId: principalIds[queue.consumer]
    principalType: 'ServicePrincipal'
    roleDefinitionId: receiverRole
  }
}]

var queueSenders = flatten(map(range(0, length(queueDefinitions)), i => map(queueDefinitions[i].?senders ?? [], sender => {
  queueIndex: i
  sender: sender
})))

resource commandSenders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for entry in queueSenders: {
  name: guid(namespace.id, queueNames[entry.queueIndex], principalIds[entry.sender], senderRole)
  scope: queues[entry.queueIndex]
  properties: {
    principalId: principalIds[entry.sender]
    principalType: 'ServicePrincipal'
    roleDefinitionId: senderRole
  }
}]

resource subscriptionConsumers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in directReadIndexes: {
  name: guid(namespace.id, subscriptionDefinitions[i].topic, subscriptionDefinitions[i].name, receiverRole)
  scope: subscriptions[i]
  properties: {
    principalId: principalIds[subscriptionDefinitions[i].consumer]
    principalType: 'ServicePrincipal'
    roleDefinitionId: receiverRole
  }
}]

// AZURE: the KEDA scaler reads the message count with a management operation (GetQueueRuntimeProperties), for which
// Data Receiver is not enough. Data Owner scoped to a single queue is the smallest permission that works.
resource scalerReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for reader in scalerQueueReaders: {
  name: guid(namespace.id, reader.queue, principalIds[reader.unit], ownerRole)
  scope: queues[indexOf(queueNames, reader.queue)]
  properties: {
    principalId: principalIds[reader.unit]
    principalType: 'ServicePrincipal'
    roleDefinitionId: ownerRole
  }
}]

resource eventGridSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(namespace.id, 'invoice-blob-events', eventGridPrincipalId, senderRole)
  scope: queues[indexOf(queueNames, 'invoice-blob-events')]
  properties: {
    principalId: eventGridPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: senderRole
  }
}

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: namespace
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [
      { category: 'OperationalLogs', enabled: true }
      { category: 'RuntimeAuditLogs', enabled: true }
    ]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output id string = namespace.id
output name string = namespace.name
output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
output invoiceBlobEventsQueueId string = queues[indexOf(queueNames, 'invoice-blob-events')].id
