# Identities and permissions

The full matrix: who (identity) can do what (role) on what (scope). The principles are in
[ADR-0010](../adr/0010-managed-identities-and-app-roles.md); an excerpt of this table is in
[00-overview.md](00-overview.md), section 9.

Identity names: `id-shopflow-{unit}-{env}-weu`, abbreviated in the tables as `id-…-{unit}`.

## Four permission systems

Permissions in ShopFlow live in four separate systems, and a role in one of them grants nothing in another.

| System | Where it is granted | Example |
|---|---|---|
| Azure RBAC (Azure services' data plane) | Bicep, resource or entity scope | Service Bus Data Sender on the `orders-events` topic |
| Cosmos DB data-plane RBAC (`sqlRoleAssignments`) | Bicep ([cosmos.bicep](../../infra/modules/data/cosmos.bicep)), database scope | Built-in Data Contributor on the `orders` database |
| Azure SQL database roles | the [grant-managed-identities.sql](../../infra/sql/grant-managed-identities.sql) script (users, membership) + the `ApplicationRoleGrants` migration (role permissions) | `app_inventory_worker`: writes in the `inventory` and `messaging` schemas |
| Entra ID app roles | [bootstrap-entra-apps.sh](../../infra/scripts/bootstrap-entra-apps.sh) | `Orders.Internal.Write` for `id-…-order-saga` |

## Common to every unit

| Role | Scope | Why |
|---|---|---|
| Monitoring Metrics Publisher | Application Insights | telemetry export with an Entra ID token (`DisableLocalAuth`) |
| App Configuration Data Reader | the App Configuration store | settings and feature flags; grants no access to secrets |

## Matrix per unit

| Identity | Resource | Scope | Role |
|---|---|---|---|
| `id-…-orders-api` | Cosmos | database `orders` | Built-in Data Contributor |
| | Redis | database | `default` policy |
| `id-…-orders-processors` | Cosmos | database `orders` | Built-in Data Contributor |
| | Service Bus | topic `orders-events` | Data Sender |
| | Service Bus | subscription `shipping-events/orders` | Data Receiver |
| | Storage `func` | account | Blob Data Owner, Queue Data Contributor, Table Data Contributor |
| `id-…-order-saga` | Cosmos | database `saga` | Built-in Data Contributor |
| | Service Bus | queues `inventory-commands`, `payments-commands`, `shipping-commands` | Data Sender |
| | Service Bus | queue `saga-replies`, subscription `orders-events/saga-start` | Data Receiver |
| | Durable Task Scheduler | task hub `order-saga` | Durable Task Data Contributor |
| | Storage `func` | account | as above |
| `id-…-inventory-api` | Azure SQL | `sqldb-inventory` | database role `app_inventory_api` (catalog reads, price and receipt writes) |
| | Redis | database | `default` policy |
| | Container Registry (shared) | registry | AcrPull |
| `id-…-inventory-worker` | Azure SQL | `sqldb-inventory` | database role `app_inventory_worker` |
| | Service Bus | queue `inventory-commands` | Data Receiver + Data Owner (the KEDA scaler reads the queue length) |
| | Service Bus | topic `inventory-events` | Data Sender |
| | Redis | database | `default` policy |
| | Container Registry | registry | AcrPull |
| `id-…-inventory-migrations` | Azure SQL | `sqldb-inventory` | `db_owner` (only the migrations job started by the pipeline) |
| | Container Registry | registry | AcrPull |
| `id-…-payments-processor` | Cosmos | database `payments` | Built-in Data Contributor |
| | Service Bus | queues `payments-commands`, `invoice-blob-events`; subscription `payments-events/invoicing` | Data Receiver |
| | Service Bus | topic `payments-events` | Data Sender |
| | Storage `docs` | container `invoices` | Blob Data Contributor |
| | Key Vault | vault | Key Vault Secrets User |
| | Storage `func` | account | as above |
| `id-…-shipping-processor` | Cosmos | database `shipping` | Built-in Data Contributor |
| | Service Bus | queue `shipping-commands` / topic `shipping-events` | Data Receiver / Data Sender |
| | Storage `docs` | container `labels` / account | Blob Data Contributor / Blob Delegator |
| | Key Vault | vault | Key Vault Secrets User |
| | Storage `func` | account | as above |
| `id-…-notifications-processor` | Cosmos | database `notifications` | Built-in Data Contributor |
| | Service Bus | queue `notifications-inbox` | Data Receiver |
| | Storage `docs` | container `invoices` / account | Blob Data Reader / Blob Delegator |
| | Communication Services | resource | Communication and Email Service Owner |
| | External ID tenant | registration `shopflow-customer-directory` | federated credential + Graph `User.Read.All` |
| | Storage `func` | account | as above |
| `id-…-readmodels-projections` | Cosmos | database `readmodels` | Built-in Data Contributor |
| | Service Bus | queue `readmodels-inbox` | Data Receiver |
| | Redis | database | `default` policy (invalidating `rm:order:*`) |
| | Storage `func` | account | as above |
| `id-…-readmodels-api` | Cosmos | database `readmodels` | Built-in Data **Reader** |
| | Storage `docs` | container `invoices` / account | Blob Data Reader / Blob Delegator |
| | Redis | database | `default` policy |
| | Container Registry | registry | AcrPull |
| `id-…-analytics-processor` | Cosmos | database `analytics` | Built-in Data Contributor |
| | Service Bus | queue `analytics-inbox` | Data Receiver |
| | Event Hubs | hubs `clickstream`, `business-events` | Data Sender + Data Receiver |
| | Storage `func` | account (Event Hubs checkpoints) | as above |

`orders-api` has no Service Bus role at all: it publishes only through the outbox, and `orders-processors` sends.

## Platform identities

| Identity | Resource | Scope | Role |
|---|---|---|---|
| `id-…-eventgrid-docs` (Event Grid system topic) | Service Bus | queue `invoice-blob-events` | Data Sender |
| | Storage `docs` | container `eventgrid-deadletter` | Blob Data Contributor |
| `id-…-evh-capture` (Event Hubs Capture) | Storage `lake` | container `capture` | Blob Data Contributor |
| `id-…-apim` | Key Vault | vault | Key Vault Secrets User (named value: the carrier webhook token) |
| | function apps | Easy Auth app roles | `Payments.Webhooks.Write`, `Carrier.Webhooks.Write`, `Analytics.Ingest` |

## App roles (HTTP)

| Entra application (audience) | Role | Held by | Checked in |
|---|---|---|---|
| `api://shopflow-orders` | `Orders.Internal.Write` | `id-…-order-saga` | `orders-api`, the policy for `/internal/orders` |
| `api://shopflow-inventory` | `Catalog.Prices.Read` | `id-…-orders-api` | `inventory-api`, `/internal/catalog/prices` |
| `api://shopflow-inventory` | `Stock.Receipts.Write` | the warehouse system | `inventory-api`, `/v1/admin/stock/receipts` |
| `api://shopflow-func-order-saga` | `Saga.Operator` | the on-call group | Easy Auth + `EasyAuthPrincipal` in `saga-status`, `saga-resolution` |
| `api://shopflow-func-payments` | `Payments.Webhooks.Write` | `id-…-apim` | `psp-webhook` (plus the HMAC signature) |
| `api://shopflow-func-shipping` | `Carrier.Webhooks.Write`, `Warehouse.Operator` | `id-…-apim`, the warehouse group | carrier webhook, labels |
| `api://shopflow-func-notifications` | `Support.Reader` | the support group | a customer's notification history |
| `api://shopflow-func-analytics` | `Analytics.Ingest`, `Analytics.Reader` | `id-…-apim`, the analysts group | clickstream, metrics |

Shop customers have no app roles: their External ID token contains `oid` (the customer identifier), and the API checks
that the order belongs to that customer.

## Pipeline (GitHub Actions, OIDC)

Created by an administrator in [infra/bootstrap](../../infra/bootstrap); the pipeline cannot grant itself permissions.

| Identity | GitHub token subject | Roles |
|---|---|---|
| `id-shopflow-github-build` | `repo:…:ref:refs/heads/main` | AcrPush on the shared registry |
| `id-shopflow-github-plan-dev-weu` | `repo:…:pull_request` | Reader on the dev groups and the shared group; "ShopFlow What-If Reader" on the subscription |
| `id-shopflow-github-deploy-{env}-weu` | `repo:…:environment:{env}` | Contributor on the environment's three groups; RBAC Administrator with an ABAC condition (only the roles on the `delegableRoles` list); AcrPull on the shared group; "ShopFlow Deployment Operator" on the subscription |

The ABAC condition means a deployment can grant an application identity e.g. Service Bus Data Sender, but not Owner or
User Access Administrator. The role list: `delegableRoles` in [roles.bicep](../../infra/shared/roles.bicep).

## People

| Entra ID group | Permissions | How they are activated |
|---|---|---|
| `sg-shopflow-sql-admins-{env}` | Entra administrator of the SQL server | PIM, for the duration of a task |
| on-call | `Saga.Operator`; Service Bus Data Receiver/Sender on the namespace (DLQ replay); Reader on the groups | PIM, for the duration of an incident |
| platform | subscription Owner (bootstrap), approving Private Link | PIM, with a second person's approval |

Assignments of people are not in Bicep: the identity team manages them in PIM, and the pipeline has no permission to change them.
