# ShopFlow

An e-commerce platform on Azure built as microservices: Orders, Inventory, Payments, Shipping, Notifications,
Customer Read Models and Analytics. Each bounded context is a separate deployable unit in this monorepo.

> The repository is for learning through code analysis. It mirrors a production enterprise project, but it is not
> meant to be run or deployed. `// AZURE:` comments explain the behavior and limits of Azure services, and `// WHY:`
> comments justify design decisions.

## Where to start

| You want to understand | Read |
|---|---|
| The architecture, flows and Azure resources | [docs/architecture/00-overview.md](docs/architecture/00-overview.md) |
| **What runs where, step by step, in concrete scenarios** | [docs/walkthroughs](docs/walkthroughs/README.md) |
| **Which patterns are used and where they live in the code** | [docs/architecture/patterns.md](docs/architecture/patterns.md) |
| The repository layout and the technology stack | [docs/architecture/01-repository-plan.md](docs/architecture/01-repository-plan.md) |
| C4 diagrams: context, containers, components | [c4-context.md](docs/architecture/c4-context.md), [c4-container.md](docs/architecture/c4-container.md), [c4-component-orders.md](docs/architecture/c4-component-orders.md) |
| Who publishes, who subscribes; the saga step by step | [message-flows.md](docs/architecture/message-flows.md), [saga.md](docs/architecture/saga.md) |
| The network and the full permission matrix | [network.md](docs/architecture/network.md), [identity-rbac.md](docs/architecture/identity-rbac.md) |
| Why it is built this way (16 decisions) | [docs/adr](docs/adr/README.md) |
| What the on-call engineer does when an alert fires | [docs/runbooks](docs/runbooks/README.md), queries in [docs/kql](docs/kql/README.md) |
| What it costs and what drives the bill | [docs/cost.md](docs/cost.md) |
| How Claude Code is set up for this repository | [CLAUDE.md](CLAUDE.md), [.claude/](.claude) (rules, agents, skills, hooks) |
| Message contracts and their versioning | [src/BuildingBlocks/ShopFlow.Contracts](src/BuildingBlocks/ShopFlow.Contracts) |
| Outbox on Cosmos DB (TransactionalBatch + Change Feed) | [CosmosUnitOfWork.cs](src/BuildingBlocks/ShopFlow.Persistence.Cosmos/CosmosUnitOfWork.cs), [CosmosOutboxRelay.cs](src/BuildingBlocks/ShopFlow.Persistence.Cosmos/CosmosOutboxRelay.cs) |
| Outbox on Azure SQL (EF Core + poller) | [MessagingDbContext.cs](src/BuildingBlocks/ShopFlow.Persistence.SqlServer/MessagingDbContext.cs), [SqlOutboxRelay.cs](src/BuildingBlocks/ShopFlow.Persistence.SqlServer/Messaging/SqlOutboxRelay.cs) |
| Poison messages, DLQ and retries | [ServiceBusMessageProcessor.cs](src/BuildingBlocks/ShopFlow.Messaging.ServiceBus/ServiceBusMessageProcessor.cs), [MessageFailureClassifier.cs](src/BuildingBlocks/ShopFlow.Messaging/Failures/MessageFailureClassifier.cs) |
| Idempotency-Key and ETag over HTTP | [IdempotencyEndpointFilter.cs](src/BuildingBlocks/ShopFlow.Web/Idempotency/IdempotencyEndpointFilter.cs), [ConcurrencyHttpExtensions.cs](src/BuildingBlocks/ShopFlow.Web/Http/ConcurrencyHttpExtensions.cs) |
| Managed Identity and App Configuration | [AzureCredentialFactory.cs](src/BuildingBlocks/ShopFlow.Azure/Identity/AzureCredentialFactory.cs), [AzureHostBuilderExtensions.cs](src/BuildingBlocks/ShopFlow.Azure/AzureHostBuilderExtensions.cs) |
| An aggregate with idempotent state transitions | [Order.cs](src/Services/Orders/ShopFlow.Orders.Domain/Orders/Order.cs) |
| The same domain error as "retry" or "DLQ" | [ShipmentEventHandlers.cs](src/Services/Orders/ShopFlow.Orders.Application/Shipping/ShipmentEventHandlers.cs) |
| Cosmos DB document schema evolution | [OrderDocument.cs](src/Services/Orders/ShopFlow.Orders.Infrastructure/Cosmos/OrderDocument.cs), [OrderDocumentMapper.cs](src/Services/Orders/ShopFlow.Orders.Infrastructure/Cosmos/OrderDocumentMapper.cs) |
| API versioning (v1/v2, Deprecation, Sunset) | [PublicOrderEndpoints.cs](src/Services/Orders/ShopFlow.Orders.Api/Endpoints/PublicOrderEndpoints.cs) |
| Change Feed + the retry policy in Functions | [OrdersOutboxRelayFunction.cs](src/Services/Orders/ShopFlow.Orders.Processors/Functions/OrdersOutboxRelayFunction.cs), [README](src/Services/Orders/ShopFlow.Orders.Processors/README.md) |
| Saga: orchestration, timeouts, compensations, operator | [OrderSagaWorkflow.cs](src/Services/OrderSaga/ShopFlow.OrderSaga/Workflow/OrderSagaWorkflow.cs), [README](src/Services/OrderSaga/ShopFlow.OrderSaga.Functions/README.md) |
| Participant replies as Durable events | [SagaReplyMapper.cs](src/Services/OrderSaga/ShopFlow.OrderSaga/Model/SagaReplyMapper.cs), [SagaMessageHandlers.cs](src/Services/OrderSaga/ShopFlow.OrderSaga/Messaging/SagaMessageHandlers.cs) |
| Testing the orchestrator without a host | [FakeOrchestrationContext.cs](src/Services/OrderSaga/tests/ShopFlow.OrderSaga.UnitTests/FakeOrchestrationContext.cs) |
| Reservation: allocation, compensation before the action ("tombstone"), expiry | [Reservation.cs](src/Services/Inventory/ShopFlow.Inventory.Domain/Stock/Reservation.cs), [ReservationHandlers.cs](src/Services/Inventory/ShopFlow.Inventory.Application/Reservations/ReservationHandlers.cs) |
| EF Core: a closed domain, rowversion as ETag, CHECK, migrations | [StockConfigurations.cs](src/Services/Inventory/ShopFlow.Inventory.Infrastructure/Persistence/Configurations/StockConfigurations.cs), [Migrations](src/Services/Inventory/ShopFlow.Inventory.Infrastructure/Persistence/Migrations) |
| HybridCache (L1 + Redis) and bounded staleness | [CatalogReaders.cs](src/Services/Inventory/ShopFlow.Inventory.Infrastructure/Catalog/CatalogReaders.cs) |
| Container Apps: a worker with KEDA, probes, shutdown, a chiseled image | [Worker/Program.cs](src/Services/Inventory/ShopFlow.Inventory.Worker/Program.cs), [Dockerfile](src/Services/Inventory/ShopFlow.Inventory.Api/Dockerfile) |
| An external provider: idempotency key, secrets from Key Vault, error classification | [PspGatewayClient.cs](src/Services/Payments/ShopFlow.Payments.Infrastructure/Psp/PspGatewayClient.cs), [PaymentCommandHandlers.cs](src/Services/Payments/ShopFlow.Payments.Application/Commands/PaymentCommandHandlers.cs) |
| A webhook with an HMAC signature and inbox deduplication | [PspWebhooks.cs](src/Services/Payments/ShopFlow.Payments.Infrastructure/Psp/PspWebhooks.cs), [PspWebhookFunction.cs](src/Services/Payments/ShopFlow.Payments.Functions/Functions/PspWebhookFunction.cs) |
| Blob Storage → Event Grid → Service Bus (an outbox for files) | [InvoicingServices.cs](src/Services/Payments/ShopFlow.Payments.Application/Invoicing/InvoicingServices.cs), [InvoiceBlobEventsFunction.cs](src/Services/Payments/ShopFlow.Payments.Functions/Functions/InvoiceBlobEventsFunction.cs) |
| Gap-free numbering in Cosmos DB (a synthetic partition) | [CosmosPaymentStores.cs](src/Services/Payments/ShopFlow.Payments.Infrastructure/Cosmos/CosmosPaymentStores.cs) |
| Two command versions handled by one handler | [ShipmentHandlers.cs](src/Services/Shipping/ShopFlow.Shipping.Application/ShipmentHandlers.cs) |
| User delegation SAS through Front Door (Storage without public access) | [BlobReadLinkFactory.cs](src/BuildingBlocks/ShopFlow.Azure/Storage/BlobReadLinkFactory.cs), [ShippingFunctions.cs](src/Services/Shipping/ShopFlow.Shipping.Functions/ShippingFunctions.cs) |
| Partner authentication in APIM and Easy Auth in Functions | [EasyAuthPrincipal.cs](src/BuildingBlocks/ShopFlow.Functions/EasyAuthPrincipal.cs), [README](src/Services/Shipping/ShopFlow.Shipping.Functions/README.md) |
| Idempotency of a side effect outside the database (e-mail): reservation, send, result | [NotificationSender.cs](src/Services/Notifications/ShopFlow.Notifications.Application/NotificationSender.cs), [CosmosNotificationLog.cs](src/Services/Notifications/ShopFlow.Notifications.Infrastructure/Cosmos/CosmosNotificationLog.cs) |
| Azure Communication Services: e-mail with an operation id, SMS "at most once" | [CommunicationChannels.cs](src/Services/Notifications/ShopFlow.Notifications.Infrastructure/Channels/CommunicationChannels.cs) |
| Identity federation to another tenant (Graph in External ID without a secret) | [GraphCustomerDirectory.cs](src/Services/Notifications/ShopFlow.Notifications.Infrastructure/Customers/GraphCustomerDirectory.cs) |
| CQRS: an order-tolerant projection (facts instead of the last status), an inbox in the document | [CustomerOrderView.cs](src/Services/CustomerReadModels/ShopFlow.CustomerReadModels.Application/CustomerOrderView.cs), [README](src/Services/CustomerReadModels/ShopFlow.CustomerReadModels.Projections/README.md) |
| The read side: authorization by partition key, a cache with an owner check, UUID v7 for "read your writes" | [ReadModelsApplication.cs](src/Services/CustomerReadModels/ShopFlow.CustomerReadModels.Application/ReadModelsApplication.cs), [CustomerOrderEndpoints.cs](src/Services/CustomerReadModels/ShopFlow.CustomerReadModels.Api/CustomerOrderEndpoints.cs) |
| Cosmos DB paging with a continuation token | [CosmosCustomerOrderStore.cs](src/Services/CustomerReadModels/ShopFlow.CustomerReadModels.Infrastructure/CosmosCustomerOrderStore.cs) |
| Event Hubs: ingest from the internet, partitioning, a forwarder from Service Bus | [Clickstream.cs](src/Services/Analytics/ShopFlow.Analytics.Application/Clickstream.cs), [EventHubsPublishers.cs](src/Services/Analytics/ShopFlow.Analytics.Infrastructure/EventHubsPublishers.cs), [IngestFunctions.cs](src/Services/Analytics/ShopFlow.Analytics.Functions/IngestFunctions.cs) |
| Stream aggregation: checkpoint and retries, Increment, exactly once through TransactionalBatch | [AggregationFunctions.cs](src/Services/Analytics/ShopFlow.Analytics.Functions/AggregationFunctions.cs), [CosmosMetricStore.cs](src/Services/Analytics/ShopFlow.Analytics.Infrastructure/CosmosMetricStore.cs) |
| Architecture tests: layers, context isolation, canary tests of the rules | [LayeringTests.cs](tests/ShopFlow.Architecture.Tests/LayeringTests.cs) |
| The messaging topology as data shared by Bicep, tests and tools | [messaging-topology.json](infra/modules/messaging/messaging-topology.json), [MessagingTopologyTests.cs](tests/ShopFlow.Contracts.Tests/MessagingTopologyTests.cs) |
| Pact: HTTP and messages, provider verification on Kestrel | [README](tests/ShopFlow.Pact.Tests/README.md), [pacts](tests/ShopFlow.Pact.Tests/pacts) |
| DLQ replay (an operator tool) | [ReplayPlanning.cs](tools/ShopFlow.DlqReplay/ReplayPlanning.cs), [README](tools/ShopFlow.DlqReplay/README.md) |
| Bicep infrastructure: layers, zero keys, per-entity roles, topology from JSON | [infra/README.md](infra/README.md), [servicebus.bicep](infra/modules/messaging/servicebus.bicep), [data.bicep](infra/layers/data.bicep) |
| OIDC bootstrap: pipeline identities, constrained RBAC delegation, custom roles | [environment.bicep](infra/bootstrap/environment.bicep), [shared.bicep](infra/bootstrap/shared.bicep), [group-access.bicep](infra/bootstrap/modules/group-access.bicep) |
| CI/CD: one job per environment, differences since the last deployment, runners in the VNet, slot swap | [.github/README.md](.github/README.md), [_deploy.yml](.github/workflows/_deploy.yml), [ci.yml](.github/workflows/ci.yml) |
| SQL migrations as a Container Apps job, role permissions in a migration | [Dockerfile.migrations](src/Services/Inventory/ShopFlow.Inventory.Infrastructure/Dockerfile.migrations), [ApplicationRoleGrants](src/Services/Inventory/ShopFlow.Inventory.Infrastructure/Persistence/Migrations), [run-migrations](.github/actions/run-migrations/action.yml) |
| A feature flag with per-customer targeting | [DependencyInjection.cs](src/Services/Notifications/ShopFlow.Notifications.Infrastructure/DependencyInjection.cs), [NotificationHandlers.cs](src/Services/Notifications/ShopFlow.Notifications.Application/NotificationHandlers.cs) |

## Structure

```text
src/BuildingBlocks/   shared libraries: contracts, messaging, persistence, Azure, web, functions
src/Services/         services: seven bounded contexts, twelve deployable units
tests/                cross-cutting tests (contracts, architecture, Pact)
tools/                operator tools (DLQ replay)
infra/                Bicep: bootstrap (administrator) and main (pipeline)
.github/              GitHub Actions pipelines, composite actions (details: .github/README.md)
docs/                 architecture and C4, patterns, walkthroughs, 16 ADRs, runbooks, KQL queries, costs
scripts/              local verification (mirrors the cloud-free part of CI)
.claude/              Claude Code configuration: rules, agents, skills, hooks
```

## Locally (optional, no cost)

Build and the unit tests work without an Azure account. The verification script runs the same checks as the
cloud-free part of `ci.yml` (format, build, tests, Pact, pending EF Core model changes):

```powershell
./scripts/verify.ps1
```

```bash
./scripts/verify.sh
```

Or step by step:

```powershell
dotnet build ShopFlow.slnx
```

```powershell
dotnet test --solution ShopFlow.slnx
```

Integration tests (Testcontainers: SQL Server and the Cosmos DB emulator) are skipped unless
`SHOPFLOW_RUN_INTEGRATION_TESTS=1` is set and Docker is running; everything else needs neither Docker nor Azure.
Contract schema snapshot tests: after a deliberate contract change, approve the new snapshots with the
`SHOPFLOW_ACCEPT_SNAPSHOTS=1` variable and include the diff of the `*.approved.json` files in the pull request.
