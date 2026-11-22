// Front Door Premium with WAF: the internet entry to APIM (api.*) and to files (files.*), both through Private Link.

import { resourceName } from '../../shared/naming.bicep'

param envName string
param regionCode string
param location string
param tags object
param publicDomain string
param apimId string
param apimGatewayHostName string
param docsStorageId string
param logAnalyticsWorkspaceId string

var hostPrefix = envName == 'prod' ? '' : '${envName}-'

resource waf 'Microsoft.Network/FrontDoorWebApplicationFirewallPolicies@2024-02-01' = {
  name: replace(resourceName('waf', 'edge', envName, regionCode), '-', '')
  location: 'global'
  tags: tags
  sku: { name: 'Premium_AzureFrontDoor' }
  properties: {
    policySettings: {
      enabledState: 'Enabled'
      // WHY: Detection mode in dev: rules log but do not block, so false positives show up in the logs before they block real
      // traffic in production.
      mode: envName == 'dev' ? 'Detection' : 'Prevention'
      requestBodyCheck: 'Enabled'
    }
    managedRules: {
      managedRuleSets: [
        { ruleSetType: 'Microsoft_DefaultRuleSet', ruleSetVersion: '2.1', ruleSetAction: 'Block' }
        { ruleSetType: 'Microsoft_BotManagerRuleSet', ruleSetVersion: '1.1' }
      ]
    }
    customRules: {
      rules: [
        {
          // A limit at the edge: a single address cannot flood the gateway (APIM has a second, finer per-client limit).
          name: 'RateLimitPerIp'
          priority: 100
          ruleType: 'RateLimitRule'
          rateLimitDurationInMinutes: 1
          rateLimitThreshold: 1200
          action: 'Block'
          matchConditions: [
            {
              matchVariable: 'RequestUri'
              operator: 'RegEx'
              matchValue: ['.*']
            }
          ]
        }
      ]
    }
  }
}

resource profile 'Microsoft.Cdn/profiles@2024-02-01' = {
  name: resourceName('afd', 'edge', envName, regionCode)
  location: 'global'
  tags: tags
  // AZURE: Premium, because only this tier has Private Link origins and WAF managed rules.
  sku: { name: 'Premium_AzureFrontDoor' }
  properties: {
    originResponseTimeoutSeconds: 60
  }
}

resource apiEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = {
  parent: profile
  name: 'api-${envName}'
  location: 'global'
  tags: tags
  properties: { enabledState: 'Enabled' }
}

resource filesEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = {
  parent: profile
  name: 'files-${envName}'
  location: 'global'
  tags: tags
  properties: { enabledState: 'Enabled' }
}

// AZURE: custom domains with a certificate managed by Front Door. Domain ownership is confirmed by a TXT record
// (_dnsauth.<domain>), and the certificate renews itself. DNS records live outside this repository (the company's public zone).
resource apiDomain 'Microsoft.Cdn/profiles/customDomains@2024-02-01' = {
  parent: profile
  name: 'api'
  properties: {
    hostName: '${hostPrefix}api.${publicDomain}'
    tlsSettings: {
      certificateType: 'ManagedCertificate'
      minimumTlsVersion: 'TLS12'
    }
  }
}

resource filesDomain 'Microsoft.Cdn/profiles/customDomains@2024-02-01' = {
  parent: profile
  name: 'files'
  properties: {
    hostName: '${hostPrefix}files.${publicDomain}'
    tlsSettings: {
      certificateType: 'ManagedCertificate'
      minimumTlsVersion: 'TLS12'
    }
  }
}

resource apimOrigins 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: profile
  name: 'apim'
  properties: {
    loadBalancingSettings: { sampleSize: 4, successfulSamplesRequired: 3 }
    healthProbeSettings: {
      // APIM gateway health endpoint (returns 200 when the gateway works), without calling backends.
      probePath: '/status-0123456789abcdef'
      probeProtocol: 'Https'
      probeRequestType: 'GET'
      probeIntervalInSeconds: 60
    }
  }
}

// AZURE: an origin through Private Link. Front Door creates its own private endpoint to the APIM gateway; the connection
// must be approved once on the APIM side (bootstrap step: az network private-endpoint-connection approve). From then on
// traffic from the edge to the gateway does not cross the internet, and APIM can have public access disabled.
resource apimOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: apimOrigins
  name: 'apim-gateway'
  properties: {
    hostName: apimGatewayHostName
    originHostHeader: apimGatewayHostName
    httpsPort: 443
    priority: 1
    weight: 1000
    enforceCertificateNameCheck: true
    sharedPrivateLinkResource: {
      privateLink: { id: apimId }
      groupId: 'Gateway'
      privateLinkLocation: location
      requestMessage: 'Front Door ${envName}'
    }
  }
}

resource docsOrigins 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: profile
  name: 'docs'
  properties: {
    loadBalancingSettings: { sampleSize: 4, successfulSamplesRequired: 3 }
  }
}

var docsBlobHost = '${last(split(docsStorageId, '/'))}.blob.${environment().suffixes.storage}'

// AZURE: files (invoices, labels) through Private Link to a Storage account without public access. The user delegation SAS
// signature covers the resource path, not the host name, so a link with the files.* host stays valid (BlobReadLinkFactory).
resource docsOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: docsOrigins
  name: 'docs-blob'
  properties: {
    hostName: docsBlobHost
    originHostHeader: docsBlobHost
    httpsPort: 443
    priority: 1
    weight: 1000
    enforceCertificateNameCheck: true
    sharedPrivateLinkResource: {
      privateLink: { id: docsStorageId }
      groupId: 'blob'
      privateLinkLocation: location
      requestMessage: 'Front Door ${envName}'
    }
  }
}

resource catalogRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: apiEndpoint
  name: 'catalog'
  properties: {
    customDomains: [{ id: apiDomain.id }]
    originGroup: { id: apimOrigins.id }
    patternsToMatch: ['/catalog/*']
    supportedProtocols: ['Https']
    httpsRedirect: 'Disabled'
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Enabled'
    // WHY: the catalog is public and the same for everyone, so edge caching offloads APIM and inventory-api.
    // The lifetime comes from the response's Cache-Control; the query string (page, category) is part of the key.
    cacheConfiguration: {
      queryStringCachingBehavior: 'UseQueryString'
      compressionSettings: {
        isCompressionEnabled: true
        contentTypesToCompress: ['application/json']
      }
    }
  }
  dependsOn: [apimOrigin]
}

resource apiRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: apiEndpoint
  name: 'api'
  properties: {
    customDomains: [{ id: apiDomain.id }]
    originGroup: { id: apimOrigins.id }
    patternsToMatch: ['/*']
    supportedProtocols: ['Https']
    httpsRedirect: 'Disabled'
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Enabled'
    // No cacheConfiguration: API responses are per customer and must not land in an edge cache.
  }
  dependsOn: [apimOrigin]
}

resource filesRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: filesEndpoint
  name: 'files'
  properties: {
    customDomains: [{ id: filesDomain.id }]
    originGroup: { id: docsOrigins.id }
    patternsToMatch: ['/invoices/*', '/labels/*']
    supportedProtocols: ['Https']
    httpsRedirect: 'Disabled'
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Enabled'
    // WHY: no cache. A private document should not sit on edge servers, even under a unique SAS key.
  }
  dependsOn: [docsOrigin]
}

resource security 'Microsoft.Cdn/profiles/securityPolicies@2024-02-01' = {
  parent: profile
  name: 'waf'
  properties: {
    parameters: {
      type: 'WebApplicationFirewall'
      wafPolicy: { id: waf.id }
      associations: [
        {
          domains: [{ id: apiDomain.id }, { id: filesDomain.id }, { id: apiEndpoint.id }, { id: filesEndpoint.id }]
          patternsToMatch: ['/*']
        }
      ]
    }
  }
}

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: profile
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [
      { category: 'FrontDoorAccessLog', enabled: true }
      { category: 'FrontDoorWebApplicationFirewallLog', enabled: true }
      { category: 'FrontDoorHealthProbeLog', enabled: false }
    ]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output frontDoorId string = profile.properties.frontDoorId
output apiHostName string = apiDomain.properties.hostName
output filesHostName string = filesDomain.properties.hostName
