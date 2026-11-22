// Bootstrap parameters for the prod environment (resource groups, pipeline identities, permissions, budget).
using 'environment.bicep'

param envName = 'prod'
param location = 'westeurope'
param regionCode = 'weu'
param githubRepository = 'shopflow-org/shopflow'
param monthlyBudget = 15000
param alertEmail = 'shopflow-oncall@shopflow.example'
