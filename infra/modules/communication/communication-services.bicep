// Azure Communication Services with the e-mail service and the sender domain.

import { resourceName } from '../../shared/naming.bicep'
import { builtInRoles } from '../../shared/roles.bicep'

param envName string
param tags object
param publicDomain string
param senderPrincipalId string

// AZURE: the Email Communication Service holds sender domains. In dev an Azure-managed domain (xxxx.azurecomm.net,
// no DNS configuration, low limits); in test and prod a custom domain whose ownership and SPF, DKIM and DMARC records
// are verified in DNS (without them mail lands in spam).
var customDomain = envName == 'dev' ? '' : (envName == 'prod' ? 'notify.${publicDomain}' : 'notify-${envName}.${publicDomain}')

resource emailService 'Microsoft.Communication/emailServices@2023-04-01' = {
  name: resourceName('ecs', '', envName, 'global')
  location: 'global'
  tags: tags
  properties: { dataLocation: 'Europe' }
}

resource domain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  parent: emailService
  name: empty(customDomain) ? 'AzureManagedDomain' : customDomain
  location: 'global'
  tags: tags
  properties: {
    domainManagement: empty(customDomain) ? 'AzureManaged' : 'CustomerManaged'
    // Transactional messages: no open or click tracking (see AcsEmailSender).
    userEngagementTracking: 'Disabled'
  }
}

resource sender 'Microsoft.Communication/emailServices/domains/senderUsernames@2023-04-01' = {
  parent: domain
  name: 'donotreply'
  properties: {
    username: 'DoNotReply'
    displayName: 'ShopFlow'
  }
}

resource communication 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: resourceName('acs', '', envName, 'global')
  location: 'global'
  tags: tags
  properties: {
    // AZURE: data location (message content, logs) in the EU because of GDPR.
    dataLocation: 'Europe'
    linkedDomains: [domain.id]
    disableLocalAuth: true
  }
}

resource senderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(communication.id, senderPrincipalId, builtInRoles.communicationAndEmailServiceOwner)
  scope: communication
  properties: {
    principalId: senderPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', builtInRoles.communicationAndEmailServiceOwner)
  }
}

output endpoint string = 'https://${communication.properties.hostName}'
output senderAddress string = 'DoNotReply@${domain.properties.mailFromSenderDomain}'
