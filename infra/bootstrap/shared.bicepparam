// Shared bootstrap parameters (image registry, build identity, custom roles).
using 'shared.bicep'

param location = 'westeurope'
param registryName = 'crshopflowsharedweu'
param githubRepository = 'shopflow-org/shopflow'
