// dev environment: the same topology as prod (Premium Service Bus, private endpoints), the smallest sizes.
using '../main.bicep'

param imageTag = readEnvironmentVariable('IMAGE_TAG', 'latest')

// Image tags of the running applications (JSON from the pipeline), so that an infrastructure deployment does not change application versions.
param imageTags = json(readEnvironmentVariable('IMAGE_TAGS', '{}'))

// true after the Private Link connections from Front Door are approved (the APIM_PRIVATE_ONLY variable of the GitHub environment).
param apimPrivateOnly = bool(readEnvironmentVariable('APIM_PRIVATE_ONLY', 'false'))

param config = {
  name: 'dev'
  location: 'westeurope'
  regionCode: 'weu'
  addressSpace: '10.10.0.0/22'
  zoneRedundant: false
  cosmos: { serverless: true, maxAutoscaleThroughput: 1000 }
  serviceBus: { capacity: 1 }
  eventHubs: { capacity: 1, autoInflateMaximum: 0 }
  sql: { skuName: 'GP_S_Gen5_2', autoPauseDelayMinutes: 60 }
  redis: { skuName: 'Balanced_B0', highAvailability: false }
  appService: { skuName: 'P0v3', minInstances: 1, maxInstances: 2 }
  apim: { capacity: 1 }
  functions: { alwaysReadyInstances: 0, maximumInstanceCount: 20 }
  monitoring: { retentionDays: 30, dailyQuotaGb: 5, samplingPercentage: 100 }
  alertEmail: 'shopflow-dev-alerts@shopflow.example'
  // The identifiers below are filled in by infra/scripts/bootstrap-entra-apps.sh (app registrations and Entra ID groups).
  sqlAdminGroupObjectId: '00000000-0000-0000-0000-000000000000'
  sqlAdminGroupName: 'sg-shopflow-sql-admins-dev'
  publicDomain: 'shopflow.example'
  githubBusinessId: '0000000'
  entra: {
    workforceTenantId: '00000000-0000-0000-0000-000000000000'
    customersTenantId: '00000000-0000-0000-0000-000000000000'
    customersAuthority: 'https://shopflowcustomers.ciamlogin.com/00000000-0000-0000-0000-000000000000/v2.0'
    customerDirectoryClientId: '00000000-0000-0000-0000-000000000000'
    apiAudiences: {
      orders: 'api://shopflow-orders-dev'
      inventory: 'api://shopflow-inventory-dev'
      readmodels: 'api://shopflow-readmodels-dev'
    }
    functionAuthClientIds: {
      'order-saga': '00000000-0000-0000-0000-000000000001'
      'payments-processor': '00000000-0000-0000-0000-000000000002'
      'shipping-processor': '00000000-0000-0000-0000-000000000003'
      'notifications-processor': '00000000-0000-0000-0000-000000000004'
      'analytics-processor': '00000000-0000-0000-0000-000000000005'
    }
  }
  partners: {
    pspBaseUrl: 'https://sandbox.psp.example/'
    carrierBaseUrl: 'https://sandbox.carrier.example/'
    smsSenderNumber: '+48000000000'
    // Documentation ranges (RFC 5737); the partners provide the real ones.
    pspIpFrom: '198.51.100.0'
    pspIpTo: '198.51.100.255'
    carrierIpFrom: '203.0.113.0'
    carrierIpTo: '203.0.113.255'
  }
}
