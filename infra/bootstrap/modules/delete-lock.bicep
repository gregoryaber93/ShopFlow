// Lock against deleting the resource group and everything in it.

param notes string

resource lock 'Microsoft.Authorization/locks@2020-05-01' = {
  name: 'do-not-delete'
  properties: {
    level: 'CanNotDelete'
    notes: notes
  }
}
