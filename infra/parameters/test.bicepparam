// test environment: sizes like dev, but Cosmos DB autoscale and a custom e-mail domain (performance and deliverability tests).
using '../main.bicep'

param imageTag = readEnvironmentVariable('IMAGE_TAG', 'latest')

// Image tags of the running applications (JSON from the pipeline), so that an infrastructure deployment does not change application versions.
param imageTags = json(readEnvironmentVariable('IMAGE_TAGS', '{}'))

// true after the Private Link connections from Front Door are approved (the APIM_PRIVATE_ONLY variable of the GitHub environment).
param apimPrivateOnly = bool(readEnvironmentVariable('APIM_PRIVATE_ONLY', 'false'))

param config = {
  name: 'test'
  location: 'westeurope'
  regionCode: 'weu'
  addressSpace: '10.20.0.0/22'
  zoneRedundant: false
  cosmos: { serverless: false, maxAutoscaleThroughput: 4000 }
  serviceBus: { capacity: 1 }
  eventHubs: { capacity: 1, autoInflateMaximum: 4 }
  sql: { skuName: 'GP_S_Gen5_2', autoPauseDelayMinutes: 120 }
  redis: { skuName: 'Balanced_B1', highAvailability: false }
  appService: { skuName: 'P0v3', minInstances: 1, maxInstances: 3 }
  apim: { capacity: 1 }
  functions: { alwaysReadyInstances: 0, maximumInstanceCount: 40 }
  monitoring: { retentionDays: 30, dailyQuotaGb: 10, samplingPercentage: 100 }
  alertEmail: 'shopflow-test-alerts@shopflow.example'
  sqlAdminGroupObjectId: '00000000-0000-0000-0000-000000000000'
  sqlAdminGroupName: 'sg-shopflow-sql-admins-test'
  publicDomain: 'shopflow.example'
  githubBusinessId: '0000000'
  entra: {
    workforceTenantId: '00000000-0000-0000-0000-000000000000'
    customersTenantId: '00000000-0000-0000-0000-000000000000'
    customersAuthority: 'https://shopflowcustomers.ciamlogin.com/00000000-0000-0000-0000-000000000000/v2.0'
    customerDirectoryClientId: '00000000-0000-0000-0000-000000000000'
    apiAudiences: {
      orders: 'api://shopflow-orders-test'
      inventory: 'api://shopflow-inventory-test'
      readmodels: 'api://shopflow-readmodels-test'
    }
    functionAuthClientIds: {
      'order-saga': '00000000-0000-0000-0000-000000000011'
      'payments-processor': '00000000-0000-0000-0000-000000000012'
      'shipping-processor': '00000000-0000-0000-0000-000000000013'
      'notifications-processor': '00000000-0000-0000-0000-000000000014'
      'analytics-processor': '00000000-0000-0000-0000-000000000015'
    }
  }
  partners: {
    pspBaseUrl: 'https://sandbox.psp.example/'
    carrierBaseUrl: 'https://sandbox.carrier.example/'
    smsSenderNumber: '+48000000000'
    pspIpFrom: '198.51.100.0'
    pspIpTo: '198.51.100.255'
    carrierIpFrom: '203.0.113.0'
    carrierIpTo: '203.0.113.255'
  }
}
