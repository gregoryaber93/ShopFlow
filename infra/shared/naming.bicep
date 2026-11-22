// Naming and tagging functions (user-defined functions), shared by all modules.
// Cloud Adoption Framework convention: {type-abbreviation}-shopflow-{component}-{env}-{region}.

// WHY: a resource that is unique in an environment (Service Bus namespace, Cosmos account, Key Vault) has no component: an
// empty component gives sbns-shopflow-prod-weu. There used to be a "core" component here, but kv-shopflow-core-prod-weu
// has 25 characters while Key Vault allows 24, so test and prod deployments would fail validation.
@export()
@description('Resource name with hyphens, e.g. app-shopflow-orders-prod-weu or (empty component) sbns-shopflow-prod-weu.')
func resourceName(abbreviation string, component string, environment string, region string) string =>
  empty(component) ? '${abbreviation}-shopflow-${environment}-${region}' : '${abbreviation}-shopflow-${component}-${environment}-${region}'

@export()
@description('Name without hyphens for resources that do not allow them (Storage: 3–24 characters, lowercase letters and digits).')
func compactName(abbreviation string, component string, environment string, region string) string =>
  toLower(take('${abbreviation}shopflow${component}${environment}${region}', 24))

@export()
@description('Mandatory tags. An Azure Policy on the subscription rejects a resource without them.')
func tags(environment string, service string, dataClassification string) object => {
  workload: 'shopflow'
  environment: environment
  service: service
  owner: 'platform-team'
  costCenter: 'CC-4711'
  dataClassification: dataClassification
  managedBy: 'bicep'
}

@export()
@description('Deterministic role assignment name: the same triple always gives the same GUID, so a redeployment creates no duplicates.')
func roleAssignmentName(scopeId string, principalId string, roleDefinitionId string) string =>
  guid(scopeId, principalId, roleDefinitionId)
