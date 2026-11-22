// Bootstrap parameters for the test environment (resource groups, pipeline identities, permissions, budget).
using 'environment.bicep'

param envName = 'test'
param location = 'westeurope'
param regionCode = 'weu'
param githubRepository = 'shopflow-org/shopflow'
param monthlyBudget = 4000
param alertEmail = 'shopflow-test-alerts@shopflow.example'
