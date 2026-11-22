// Bootstrap parameters for the dev environment (resource groups, pipeline identities, permissions, budget).
using 'environment.bicep'

param envName = 'dev'
param location = 'westeurope'
param regionCode = 'weu'
param githubRepository = 'shopflow-org/shopflow'
param monthlyBudget = 2500
param alertEmail = 'shopflow-dev-alerts@shopflow.example'
