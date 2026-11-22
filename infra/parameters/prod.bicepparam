// prod environment: zone redundancy, autoscale, always-ready instances, longer log retention.
using '../main.bicep'

param imageTag = readEnvironmentVariable('IMAGE_TAG')

// Image tags of the running applications (JSON from the pipeline), so that an infrastructure deployment does not change application versions.
param imageTags = json(readEnvironmentVariable('IMAGE_TAGS', '{}'))

// true after the Private Link connections from Front Door are approved (the APIM_PRIVATE_ONLY variable of the GitHub environment).
param apimPrivateOnly = bool(readEnvironmentVariable('APIM_PRIVATE_ONLY', 'false'))

param config = {
  name: 'prod'
  location: 'westeurope'
  regionCode: 'weu'
  addressSpace: '10.30.0.0/22'
  zoneRedundant: true
  cosmos: { serverless: false, maxAutoscaleThroughput: 10000 }
  serviceBus: { capacity: 2 }
  eventHubs: { capacity: 2, autoInflateMaximum: 10 }
  sql: { skuName: 'GP_Gen5_4', autoPauseDelayMinutes: -1 }
  redis: { skuName: 'Balanced_B5', highAvailability: true }
  // AZURE: a zone-redundant App Service plan requires at least three instances.
  appService: { skuName: 'P1v3', minInstances: 3, maxInstances: 10 }
  apim: { capacity: 2 }
  functions: { alwaysReadyInstances: 1, maximumInstanceCount: 100 }
  // 25% trace sampling: with production traffic full traces would cost more than the rest of monitoring.
  monitoring: { retentionDays: 90, dailyQuotaGb: -1, samplingPercentage: 25 }
  alertEmail: 'shopflow-oncall@shopflow.example'
  sqlAdminGroupObjectId: '00000000-0000-0000-0000-000000000000'
  sqlAdminGroupName: 'sg-shopflow-sql-admins-prod'
  publicDomain: 'shopflow.example'
  githubBusinessId: '0000000'
  entra: {
    workforceTenantId: '00000000-0000-0000-0000-000000000000'
    customersTenantId: '00000000-0000-0000-0000-000000000000'
    customersAuthority: 'https://shopflowcustomers.ciamlogin.com/00000000-0000-0000-0000-000000000000/v2.0'
    customerDirectoryClientId: '00000000-0000-0000-0000-000000000000'
    apiAudiences: {
      orders: 'api://shopflow-orders'
      inventory: 'api://shopflow-inventory'
      readmodels: 'api://shopflow-readmodels'
    }
    functionAuthClientIds: {
      'order-saga': '00000000-0000-0000-0000-000000000021'
      'payments-processor': '00000000-0000-0000-0000-000000000022'
      'shipping-processor': '00000000-0000-0000-0000-000000000023'
      'notifications-processor': '00000000-0000-0000-0000-000000000024'
      'analytics-processor': '00000000-0000-0000-0000-000000000025'
    }
  }
  partners: {
    pspBaseUrl: 'https://api.psp.example/'
    carrierBaseUrl: 'https://api.carrier.example/'
    smsSenderNumber: '+48000000000'
    pspIpFrom: '198.51.100.0'
    pspIpTo: '198.51.100.255'
    carrierIpFrom: '203.0.113.0'
    carrierIpTo: '203.0.113.255'
  }
}
