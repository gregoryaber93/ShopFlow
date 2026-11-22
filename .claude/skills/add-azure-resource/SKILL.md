---
name: add-azure-resource
description: Adds an Azure resource to ShopFlow's Bicep the house way - module, layer, naming, per-environment parameters, private endpoint and DNS, managed-identity roles, diagnostics, app settings, docs and cost - without deploying or compiling anything. Use when the user wants a new Azure service, a new instance (storage account, container, queue host, function app) or a new permission.
argument-hint: "<resource type> <purpose>"
---

# Add an Azure resource (Bicep only, no deployment)

Zero-cost constraint: write and review Bicep, never run `az`, what-if or install the Bicep CLI. CI
(`infra-validate.yml`) is where it would compile. Read `.claude/rules/infra-bicep.md` and `infra/README.md` first.

## 1. Place it

| Resource kind | Layer (resource group) | Module folder |
|---|---|---|
| network, DNS, monitoring, identities | `layers/core.bicep` (`rg-…-core`) | `modules/network`, `modules/monitoring`, `modules/identity` |
| data stores, Key Vault, App Configuration, ACS | `layers/data.bicep` (`rg-…-data`, delete lock) | `modules/data`, `modules/config`, `modules/communication` |
| Service Bus, Event Hubs, Event Grid | `layers/messaging.bicep` (`rg-…-data`) | `modules/messaging` |
| compute (App Service, Container Apps, Functions, DTS) | `layers/apps.bicep` (`rg-…-apps`) | `modules/compute` |
| APIM, Front Door | `layers/edge.bicep` (`rg-…-apps`) | `modules/edge` |
| alerts, workbook | `layers/operations.bicep` (`rg-…-core`) | `modules/operations` |

Reuse an existing module when the resource type already has one (e.g. `modules/data/storage.bicep` for another
account); create a module only for a new type.

## 2. Module checklist

- Parameters with `@description`, typed (user-defined types from `shared/types.bicep` where a shape exists).
- Name: `resourceName('<CAF abbreviation>', '<component>', envName, regionAbbreviation)` or `compactName(...)`;
  tags: `tags(envName, '<service>', '<dataClassification>')`.
- Security: local auth/keys disabled, `publicNetworkAccess: 'Disabled'`, TLS 1.2, a private endpoint through
  `modules/network/private-endpoint.bicep` with the right `groupId`, and the private DNS zone added to
  `modules/network/private-dns-zones.bicep` if new.
- Diagnostic settings to the Log Analytics workspace (all logs that matter + AllMetrics).
- Role assignments inside the module or the layer, scoped to the resource (or a child: container, queue), using
  `roleAssignmentName()` and role ids from `shared/roles.bicep`. If the pipeline must grant a role it never granted
  before, add it to `delegableRoles` (this changes `bootstrap/`, so an administrator must redeploy the bootstrap).
- Outputs: ids and endpoints only, never keys or connection strings.

## 3. Per-environment configuration

New sizing knobs go into the `environmentConfig` type and all three `parameters/{dev,test,prod}.bicepparam` files:
dev minimal (serverless / consumption / scale to zero), prod zone-redundant where the service supports it.

## 4. Wire the application

- App settings (in `layers/apps.bicep`): endpoint + `__credential=managedidentity` + `__clientId`, following the
  existing `ServiceBus__fullyQualifiedNamespace` / `Cosmos__accountEndpoint` pattern. No secrets.
- C#: register the client in the context's Infrastructure with `AzureCredentialFactory` / `Microsoft.Extensions.Azure`,
  options validated on start, behind an application port.

## 5. Document

`docs/architecture/00-overview.md` (resource list), `network.md` (private endpoint, DNS zone),
`identity-rbac.md` (who gets which role on it), `docs/cost.md` (cost per environment), the service README (settings),
and an ADR if the choice of service is not obvious (`/write-adr`). Ask the `bicep-reviewer` agent for a review.
