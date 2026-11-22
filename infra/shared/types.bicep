// Environment configuration types (user-defined types). The dev/test/prod parameter file must match this shape,
// so a typo in a field name or a missing value is a compile error, not a surprise during deployment.

@export()
type environmentName = 'dev' | 'test' | 'prod'

@export()
@description('Settings in which the environments differ. Everything else (topology, roles, network) is identical.')
type environmentConfig = {
  name: environmentName

  @description('Azure region, e.g. westeurope.')
  location: string

  @description('Region abbreviation in resource names (CAF), e.g. weu.')
  regionCode: string

  @description('VNet /22 address space, different per environment (allows later peering).')
  addressSpace: string

  @description('Zone redundancy wherever the service offers it (Cosmos, SQL, Service Bus, App Service, Storage ZRS).')
  zoneRedundant: bool

  cosmos: {
    @description('Serverless (pay per RU) in dev; autoscale with a cap in test/prod.')
    serverless: bool
    @description('Upper autoscale limit in RU/s per database (ignored for serverless).')
    maxAutoscaleThroughput: int
  }

  serviceBus: {
    @description('Number of Premium units (messaging units).')
    capacity: int
  }

  eventHubs: {
    @description('Throughput units (TU) in the Standard tier.')
    capacity: int
    @description('0 = auto-inflate disabled.')
    autoInflateMaximum: int
  }

  sql: {
    @description('E.g. GP_S_Gen5_2 (serverless) or GP_Gen5_4 (provisioned).')
    skuName: string
    @description('Minutes of inactivity before auto-pause; -1 disables it (serverless only).')
    autoPauseDelayMinutes: int
  }

  redis: {
    @description('Azure Managed Redis SKU, e.g. Balanced_B0, Balanced_B5.')
    skuName: string
    highAvailability: bool
  }

  appService: {
    skuName: string
    minInstances: int
    maxInstances: int
  }

  apim: {
    @description('API Management Standard v2 units.')
    capacity: int
  }

  functions: {
    @description('Always-ready instances for the HTTP function group (fixed cost, no cold start).')
    alwaysReadyInstances: int
    maximumInstanceCount: int
  }

  monitoring: {
    retentionDays: int
    @description('Daily Log Analytics ingestion cap in GB; -1 for no cap.')
    dailyQuotaGb: int
    @description('Percentage of sampled traces (0–100).')
    samplingPercentage: int
  }

  @description('E-mail address of the on-call group for alerts.')
  alertEmail: string

  @description('Object ID of the Entra ID group of SQL administrators (sign-in only through Entra ID).')
  sqlAdminGroupObjectId: string

  @description('Name of the SQL administrators group shown as the login.')
  sqlAdminGroupName: string

  @description('Shop domain for Front Door, e.g. shopflow.example.')
  publicDomain: string

  @description('Identifier (databaseId) of the GitHub organization or enterprise whose runners get network interfaces in the VNet.')
  githubBusinessId: string

  @description('Entra ID app registrations (created by the infra/scripts/bootstrap-entra-apps.sh script, not in Bicep).')
  entra: entraConfig

  @description('Addresses of external providers (sandbox outside prod).')
  partners: {
    pspBaseUrl: string
    carrierBaseUrl: string
    smsSenderNumber: string
    @description('Address ranges partners send webhooks from (ip-filter in APIM).')
    pspIpFrom: string
    pspIpTo: string
    carrierIpFrom: string
    carrierIpTo: string
  }
}

@export()
@description('Values from the app registrations. WHY: Bicep does not create Entra ID registrations (a different lifecycle and different permissions than the subscription), so it gets their identifiers as data.')
type entraConfig = {
  workforceTenantId: string
  customersTenantId: string
  @description('E.g. https://shopflowcustomers.ciamlogin.com/<tenantId>/v2.0')
  customersAuthority: string
  @description('Registration in the External ID tenant with a federated credential for Notifications (Graph).')
  customerDirectoryClientId: string
  @description('Application ID URIs of the APIs (token audiences).')
  apiAudiences: {
    orders: string
    inventory: string
    readmodels: string
  }
  @description('App registrations for Easy Auth of HTTP functions: unit → clientId.')
  functionAuthClientIds: object
}

@export()
@description('Managed identity in the shape the layers need: resource, principal (RBAC) and clientId (application configuration).')
type identityReference = {
  id: string
  principalId: string
  clientId: string
}
