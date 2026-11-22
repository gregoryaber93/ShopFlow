// APIM global policy and a named value with the Front Door profile identifier.
// WHY: a separate module deployed after Front Door. Front Door needs APIM as an origin, and the global policy needs the
// Front Door profile identifier (frontDoorId). The APIM → Front Door → this module order breaks the cycle.

param apimName string
param frontDoorId string

resource apim 'Microsoft.ApiManagement/service@2024-05-01' existing = {
  name: apimName
}

resource frontDoorIdValue 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'frontdoor-id'
  properties: {
    displayName: 'frontdoor-id'
    value: frontDoorId
    secret: false
  }
}

resource globalPolicy 'Microsoft.ApiManagement/service/policies@2024-05-01' = {
  parent: apim
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('../../apim/policies/global.xml')
  }
  dependsOn: [frontDoorIdValue]
}
