# Patterns catalog

Every pattern below is implemented in this repository. Each entry says which problem it solves here, where the code
is, what it costs, and which [walkthrough](../walkthroughs/README.md) shows it in motion. Names follow the usual
references: *Enterprise Integration Patterns* (EIP), the Azure Architecture Center cloud design patterns, and
domain-driven design. Decisions behind them are in the [ADRs](../adr/README.md).

## Index

| Category | Patterns |
|---|---|
| [Architecture and code structure](#architecture-and-code-structure) | bounded contexts as deployable units, ports and adapters, aggregate with idempotent transitions, result object, architecture tests |
| [Messaging](#messaging) | transactional outbox, idempotent receiver, competing consumers, sessions, content-based routing, correlation identifier, retry vs. dead letter, dead letter channel and replay, topology as data, versioned contracts, choreography |
| [Distributed consistency](#distributed-consistency) | saga orchestration, compensating transaction, tombstone, optimistic concurrency, idempotency key for HTTP |
| [Data](#data) | CQRS, order-tolerant projection, read your writes, cache-aside, denormalized snapshot, document schema evolution, gap-free numbering, file outbox, exactly-once marker, event streaming, idempotent side effects |
| [Integration and edge](#integration-and-edge) | gateway offloading, secure webhook intake, anti-corruption layer, valet key, resilience pipeline, API versioning |
| [Security and operations](#security-and-operations) | managed identity with zero keys, workload identity federation, private endpoints, feature flags, health endpoints, queue-based load leveling |
| [Delivery](#delivery) | build once deploy many, deployment strategies, expand-contract migrations, consumer-driven contracts, layered infrastructure as code |

---

## Architecture and code structure

### Bounded contexts as deployable units

**Problem:** one model of "order" for sales, warehouse, payments and shipping becomes a ball of mud.
**In ShopFlow:** seven contexts (Orders, Inventory, Payments, Shipping, Notifications, Customer Read Models, Analytics),
twelve deployable units, each with its own store. Contexts share only `ShopFlow.Contracts` (messages) and HTTP APIs.
**Code:** [src/Services](../../src/Services), [00-overview.md](00-overview.md), [c4-container.md](c4-container.md).
**Trade-off:** eventual consistency between contexts; more moving parts. [ADR-0001](../adr/0001-monorepo.md), [ADR-0003](../adr/0003-compute-per-workload.md).

### Ports and adapters (hexagonal layers)

**Problem:** business rules tangled with Azure SDKs cannot be tested or replaced.
**In ShopFlow:** `Domain` (no dependencies but SharedKernel) → `Application` (use cases, ports as interfaces) →
`Infrastructure` (`internal sealed` adapters for Cosmos, SQL, Service Bus, PSP, carrier) → hosts (`Api`, `Functions`,
`Worker`) that only wire things.
**Code:** e.g. [Orders Ports.cs](../../src/Services/Orders/ShopFlow.Orders.Application/Abstractions/Ports.cs) and
[CosmosOrderRepository.cs](../../src/Services/Orders/ShopFlow.Orders.Infrastructure/Cosmos/CosmosOrderRepository.cs).
**Walkthrough:** every one; start with [01](../walkthroughs/01-place-order.md).

### Aggregate with idempotent state transitions

**Problem:** at-least-once delivery means the same command or event reaches an aggregate twice.
**In ShopFlow:** aggregates guard their invariants and treat "already in the target state" as success
(`RequestCancellation` twice is a no-op; `Confirm` on `CancellationRequested` is a business conflict).
**Code:** [Order.cs](../../src/Services/Orders/ShopFlow.Orders.Domain/Orders/Order.cs),
[Reservation.cs](../../src/Services/Inventory/ShopFlow.Inventory.Domain/Stock/Reservation.cs),
[Payment.cs](../../src/Services/Payments/ShopFlow.Payments.Domain/Payment.cs).
**Walkthrough:** [03](../walkthroughs/03-customer-cancellation.md).

### Result object for expected failures

**Problem:** exceptions for "the cart is empty" make control flow invisible and slow.
**In ShopFlow:** expected business outcomes are `Result<T>` with a typed `Error` (validation, not found, conflict) that
maps to RFC 9457 problem details; exceptions remain for the unexpected and for the messaging failure classifier.
**Code:** [Result.cs](../../src/BuildingBlocks/ShopFlow.SharedKernel/Results/Result.cs),
[Error.cs](../../src/BuildingBlocks/ShopFlow.SharedKernel/Results/Error.cs),
[ResultHttpExtensions.cs](../../src/BuildingBlocks/ShopFlow.Web/Http/ResultHttpExtensions.cs).

### Architecture tests as fitness functions

**Problem:** layering rules written only in a wiki erode with the first deadline.
**In ShopFlow:** NetArchTest rules fail the build when a layer or context references what it must not; a canary test
proves each rule can actually detect a violation.
**Code:** [LayeringTests.cs](../../tests/ShopFlow.Architecture.Tests/LayeringTests.cs),
[RoutingTopologyTests.cs](../../tests/ShopFlow.Architecture.Tests/RoutingTopologyTests.cs).

---

## Messaging

### Transactional outbox

**Problem:** saving state and publishing a message are two systems; a crash in between loses the event or publishes
a lie.
**In ShopFlow:** business code enqueues messages into `IOutbox`; the unit of work writes them with the state change
atomically (Cosmos `TransactionalBatch` in the aggregate's partition, or one SQL transaction). A relay publishes later
(Change Feed trigger, or a SQL poller that claims batches), with `MessageId` = outbox record id.
**Code:** [CosmosUnitOfWork.cs](../../src/BuildingBlocks/ShopFlow.Persistence.Cosmos/CosmosUnitOfWork.cs),
[CosmosOutboxRelay.cs](../../src/BuildingBlocks/ShopFlow.Persistence.Cosmos/CosmosOutboxRelay.cs),
[SqlOutboxRelay.cs](../../src/BuildingBlocks/ShopFlow.Persistence.SqlServer/Messaging/SqlOutboxRelay.cs).
**Trade-off:** publication latency (seconds), at-least-once publishing. [ADR-0009](../adr/0009-outbox-and-inbox.md).
**Walkthrough:** [01](../walkthroughs/01-place-order.md), [06](../walkthroughs/06-catalog-price-change.md).

### Idempotent receiver

**Problem:** every Azure broker delivers at least once; relays republish; operators replay DLQs.
**In ShopFlow:** three layers: broker duplicate detection (10 min, deterministic `MessageId`), an **inbox** entry saved
in the same transaction as the handler's state change (a repeat fails with `DuplicateMessageException` and completes
without effect), and natural idempotency in the domain. The read model keeps its inbox inside the view document.
**Code:** [MessageDispatcher.cs](../../src/BuildingBlocks/ShopFlow.Messaging/Handling/MessageDispatcher.cs),
[CosmosInbox.cs](../../src/BuildingBlocks/ShopFlow.Persistence.Cosmos/CosmosInbox.cs),
[DeterministicGuid.cs](../../src/BuildingBlocks/ShopFlow.Messaging/DeterministicGuid.cs).
**Walkthrough:** [01](../walkthroughs/01-place-order.md), [05](../walkthroughs/05-poison-message-and-replay.md).

### Competing consumers

**Problem:** one consumer cannot keep up with a queue, and a single instance is a single point of failure.
**In ShopFlow:** many instances read the same queue; Functions scale on queue length, `inventory-worker` scales with
KEDA. Consumer groups play the equivalent role for Event Hubs readers.
**Code:** [ServiceBusQueueConsumer.cs](../../src/BuildingBlocks/ShopFlow.Messaging.ServiceBus/ServiceBusQueueConsumer.cs),
[Worker/Program.cs](../../src/Services/Inventory/ShopFlow.Inventory.Worker/Program.cs), KEDA rule in
[apps.bicep](../../infra/layers/apps.bicep).
**Walkthrough:** [01](../walkthroughs/01-place-order.md), [08](../walkthroughs/08-clickstream-analytics.md).

### Ordered delivery with sessions

**Problem:** two replies for the same order processed in parallel corrupt its state.
**In ShopFlow:** messages implementing `IOrderScoped` get `SessionId = orderId`; session-enabled queues
(`payments-commands`, `saga-replies`, `readmodels-inbox`) process one order's messages in sequence and different orders
in parallel.
**Code:** [IMessage.cs](../../src/BuildingBlocks/ShopFlow.Contracts/IMessage.cs) (`IOrderScoped`),
[SagaMessagingFunctions.cs](../../src/Services/OrderSaga/ShopFlow.OrderSaga.Functions/Functions/SagaMessagingFunctions.cs).
**Trade-off:** a poison message blocks its session until it is dead-lettered.

### Content-based routing with subscription filters

**Problem:** consumers should receive only the messages they handle, without the publisher knowing them.
**In ShopFlow:** one topic per publishing context; subscriptions filter on `Subject` (correlation filter for one type,
SQL `sys.Label IN (...)` for several, `ContractVersion` for version selection) and auto-forward to the consumer's inbox
queue. The publisher never changes when a consumer is added.
**Code:** [messaging-topology.json](../../infra/modules/messaging/messaging-topology.json),
[servicebus.bicep](../../infra/modules/messaging/servicebus.bicep). **Docs:** [message-flows.md](message-flows.md).

### Correlation identifier

**Problem:** a trace ends where an asynchronous hop begins; support needs the whole story of one order.
**In ShopFlow:** every envelope carries `CorrelationId` (constant for an order), `CausationId` (the message that caused
it) and W3C `traceparent`; HTTP responses expose `X-Correlation-Id`.
**Code:** [MessageEnvelope.cs](../../src/BuildingBlocks/ShopFlow.Messaging/MessageEnvelope.cs),
[MessageEnvelopeFactory.cs](../../src/BuildingBlocks/ShopFlow.Messaging/MessageEnvelopeFactory.cs),
[CorrelationMiddleware.cs](../../src/BuildingBlocks/ShopFlow.Web/Correlation/CorrelationMiddleware.cs),
query [order-trace.kql](../kql/order-trace.kql).

### Retry vs. dead letter

**Problem:** a transient failure treated as permanent loses valid messages; a permanent one treated as transient burns
retries and blocks sessions.
**In ShopFlow:** one classifier decides (pluggable rules per adapter); transient → hold the lock with backoff and
redeliver; permanent → dead-letter immediately with a reason. Domain code chooses by exception type
(`OutOfOrderMessageException` vs. `PermanentMessageException`).
**Code:** [MessageFailureClassifier.cs](../../src/BuildingBlocks/ShopFlow.Messaging/Failures/MessageFailureClassifier.cs),
[ServiceBusMessageProcessor.cs](../../src/BuildingBlocks/ShopFlow.Messaging.ServiceBus/ServiceBusMessageProcessor.cs),
[ShipmentEventHandlers.cs](../../src/Services/Orders/ShopFlow.Orders.Application/Shipping/ShipmentEventHandlers.cs).
**Walkthrough:** [04](../walkthroughs/04-shipping-delivery-invoice.md), [05](../walkthroughs/05-poison-message-and-replay.md).

### Dead letter channel and replay

**Problem:** messages in a DLQ need a safe way back once the cause is fixed.
**In ShopFlow:** an operator tool peeks and replays with filters and a dry run, keeps `MessageId`, respects the duplicate
detection window, limits replays, sends before deleting, and targets directly read subscriptions through a
`ReplayTarget` property and generated rules.
**Code:** [tools/ShopFlow.DlqReplay](../../tools/ShopFlow.DlqReplay/README.md). **Runbook:** [dlq-growth.md](../runbooks/dlq-growth.md).
**Walkthrough:** [05](../walkthroughs/05-poison-message-and-replay.md).

### Topology as data

**Problem:** queue names repeated in Bicep, code, tests and tools drift apart.
**In ShopFlow:** one JSON file declares every entity, rule, sender and consumer; Bicep builds entities and per-entity
roles from it, the replay tool embeds it, and tests compare it with the constants in code.
**Code:** [messaging-topology.json](../../infra/modules/messaging/messaging-topology.json),
[MessagingTopology.cs](../../src/BuildingBlocks/ShopFlow.Contracts/MessagingTopology.cs),
[MessagingTopologyTests.cs](../../tests/ShopFlow.Contracts.Tests/MessagingTopologyTests.cs).

### Versioned contracts and upcasting

**Problem:** independently deployed consumers and week-old messages in DLQs must survive a breaking change.
**In ShopFlow:** additive changes within a version; a breaking change is a new `V<n>` type published side by side
behind a feature flag; each consumer picks one version; an upcaster converts old messages replayed later. JSON schema
snapshots catch accidental changes.
**Code:** [ShopFlow.Contracts](../../src/BuildingBlocks/ShopFlow.Contracts),
[OrderSubmittedV1ToV2Upcaster.cs](../../src/BuildingBlocks/ShopFlow.Contracts/Orders/OrderSubmittedV1ToV2Upcaster.cs),
[ContractSchemaTests.cs](../../tests/ShopFlow.Contracts.Tests/ContractSchemaTests.cs).
[ADR-0012](../adr/0012-contract-versioning.md). **Walkthrough:** [10](../walkthroughs/10-contract-v2-rollout.md).

### Choreography

**Problem:** after the order is confirmed, invoicing, shipping updates, notifications and read models are independent
reactions; a coordinator would only add coupling.
**In ShopFlow:** contexts subscribe to each other's events (`OrderConfirmed` → Inventory commits the reservation;
`ShipmentDelivered` → Orders completes the order). Orchestration is used only where compensation is needed.
**Code:** [ShipmentEventsFunction.cs](../../src/Services/Orders/ShopFlow.Orders.Processors/Functions/ShipmentEventsFunction.cs),
`OrderConfirmedHandler` in [ReservationHandlers.cs](../../src/Services/Inventory/ShopFlow.Inventory.Application/Reservations/ReservationHandlers.cs).
[ADR-0008](../adr/0008-saga-orchestration-vs-choreography.md). **Walkthrough:** [04](../walkthroughs/04-shipping-delivery-invoice.md).

---

## Distributed consistency

### Saga orchestration

**Problem:** placing an order spans four services with no distributed transaction.
**In ShopFlow:** a Durable Functions orchestrator (Durable Task Scheduler backend) sends commands, waits for replies as
external events with durable timers, and drives compensation; a process manager with its state in event-sourced
history, plus a Cosmos projection for support.
**Code:** [OrderSagaWorkflow.cs](../../src/Services/OrderSaga/ShopFlow.OrderSaga/Workflow/OrderSagaWorkflow.cs),
[SagaReplyMapper.cs](../../src/Services/OrderSaga/ShopFlow.OrderSaga/Model/SagaReplyMapper.cs). **Docs:** [saga.md](saga.md).
**Walkthrough:** [01](../walkthroughs/01-place-order.md), [02](../walkthroughs/02-payment-declined-compensation.md), [03](../walkthroughs/03-customer-cancellation.md).

### Compensating transaction

**Problem:** completed steps of a failed saga must be undone, even when we do not know whether they completed.
**In ShopFlow:** the compensation is registered **before** the command is sent and removed only on a clear refusal;
compensations are state-tolerant and run in reverse order; a failed compensation escalates to an operator.
**Code:** `ExecuteStepAsync`, `RunCompensationsAsync` in
[OrderSagaWorkflow.cs](../../src/Services/OrderSaga/ShopFlow.OrderSaga/Workflow/OrderSagaWorkflow.cs).
**Walkthrough:** [02](../walkthroughs/02-payment-declined-compensation.md).

### Tombstone for compensation before action

**Problem:** a compensation (`ReleaseStock`, `VoidPayment`) can overtake the command it compensates.
**In ShopFlow:** compensating a non-existent reservation or payment stores a cancelled/voided tombstone, so the late
original command is refused instead of creating state nobody will clean up.
**Code:** `Reservation.Tombstone` in [Reservation.cs](../../src/Services/Inventory/ShopFlow.Inventory.Domain/Stock/Reservation.cs),
`Payment.VoidedTombstone` in [Payment.cs](../../src/Services/Payments/ShopFlow.Payments.Domain/Payment.cs).
**Walkthrough:** [02](../walkthroughs/02-payment-declined-compensation.md).

### Optimistic concurrency (ETag)

**Problem:** two writers (a customer and the saga, two managers) overwrite each other's changes.
**In ShopFlow:** every mutable resource has a version (Cosmos `_etag`, SQL `rowversion`) exposed as an HTTP `ETag`;
updates require `If-Match` (428 without it, 412 on mismatch); domain rules decide who wins a race.
**Code:** [ConcurrencyHttpExtensions.cs](../../src/BuildingBlocks/ShopFlow.Web/Http/ConcurrencyHttpExtensions.cs),
[OrderCommandExecutor.cs](../../src/Services/Orders/ShopFlow.Orders.Application/Orders/OrderCommandExecutor.cs),
[BackofficeOperations.cs](../../src/Services/Inventory/ShopFlow.Inventory.Application/Catalog/BackofficeOperations.cs).
**Walkthrough:** [03](../walkthroughs/03-customer-cancellation.md), [06](../walkthroughs/06-catalog-price-change.md).

### Idempotency key for HTTP requests

**Problem:** a client that retries `POST /orders` after a timeout must not create two orders.
**In ShopFlow:** `Idempotency-Key` is required on order submission; the filter stores the key before the handler runs
and replays the stored response (`Idempotent-Replayed` header) for a repeat; the same idea is used towards the PSP.
**Code:** [IdempotencyEndpointFilter.cs](../../src/BuildingBlocks/ShopFlow.Web/Idempotency/IdempotencyEndpointFilter.cs),
[CosmosIdempotencyStore.cs](../../src/Services/Orders/ShopFlow.Orders.Api/Idempotency/CosmosIdempotencyStore.cs),
[PspGatewayClient.cs](../../src/Services/Payments/ShopFlow.Payments.Infrastructure/Psp/PspGatewayClient.cs).
**Walkthrough:** [01](../walkthroughs/01-place-order.md).

---

## Data

### CQRS and materialized view

**Problem:** "my orders" needs data from three contexts and a partition key the write model does not have.
**In ShopFlow:** a separate read side (projections + API) keeps one denormalized document per order in the customer's
partition, built from events.
**Code:** [CustomerOrderView.cs](../../src/Services/CustomerReadModels/ShopFlow.CustomerReadModels.Application/CustomerOrderView.cs),
[CustomerOrderEndpoints.cs](../../src/Services/CustomerReadModels/ShopFlow.CustomerReadModels.Api/CustomerOrderEndpoints.cs).
[ADR-0007](../adr/0007-cosmos-partition-keys.md). **Walkthrough:** [07](../walkthroughs/07-my-orders-read-model.md).

### Order-tolerant projection

**Problem:** events from different topics arrive in any order; "status = last event" moves orders backwards.
**In ShopFlow:** the view stores facts (`ConfirmedAt`, `DispatchedAt`, …) and derives the status from them.
**Code:** [CustomerOrderView.cs](../../src/Services/CustomerReadModels/ShopFlow.CustomerReadModels.Application/CustomerOrderView.cs).
**Walkthrough:** [07](../walkthroughs/07-my-orders-read-model.md).

### Read your writes with UUID v7

**Problem:** right after placing an order the read model may not have it yet; a plain 404 confuses the client.
**In ShopFlow:** ids are UUID v7 (time-ordered); a missing document with a recent id returns 404 with `Retry-After` and
a "projection pending" problem type.
**Code:** `UuidV7`, `CustomerOrderQueries` in
[ReadModelsApplication.cs](../../src/Services/CustomerReadModels/ShopFlow.CustomerReadModels.Application/ReadModelsApplication.cs).
**Walkthrough:** [07](../walkthroughs/07-my-orders-read-model.md).

### Cache-aside with bounded staleness

**Problem:** the catalog is read far more than it changes; SQL should not carry product pages.
**In ShopFlow:** HybridCache (process memory L1 + Azure Managed Redis L2) with stampede protection; invalidation by key
and tag after the commit; other replicas may serve L1 for up to 30 s. Checkout prices bypass the cache. The read model
caches order details with an owner check.
**Code:** [CatalogReaders.cs](../../src/Services/Inventory/ShopFlow.Inventory.Infrastructure/Catalog/CatalogReaders.cs).
[ADR-0016](../adr/0016-redis-scope-and-isolation.md). **Walkthrough:** [06](../walkthroughs/06-catalog-price-change.md), [07](../walkthroughs/07-my-orders-read-model.md).

### Denormalized snapshot in the aggregate

**Problem:** an order must keep the price and name the customer saw, even after the catalog changes.
**In ShopFlow:** order lines copy SKU, name and unit price at submission.
**Code:** [OrderLine.cs](../../src/Services/Orders/ShopFlow.Orders.Domain/Orders/OrderLine.cs).
**Walkthrough:** [06](../walkthroughs/06-catalog-price-change.md).

### Document schema evolution

**Problem:** Cosmos documents written by old code live for years.
**In ShopFlow:** documents carry a schema version; mappers read every old version and write the newest (upgrade on
write, no mass migration).
**Code:** [OrderDocument.cs](../../src/Services/Orders/ShopFlow.Orders.Infrastructure/Cosmos/OrderDocument.cs),
[OrderDocumentMapper.cs](../../src/Services/Orders/ShopFlow.Orders.Infrastructure/Cosmos/OrderDocumentMapper.cs).

### Gap-free numbering with a synthetic partition

**Problem:** invoice numbers must be sequential without gaps; Cosmos has no sequences.
**In ShopFlow:** a counter document per month in a synthetic partition (`invoice-numbers:yyyy-MM`), incremented in a
`TransactionalBatch` with optimistic concurrency.
**Code:** [CosmosPaymentStores.cs](../../src/Services/Payments/ShopFlow.Payments.Infrastructure/Cosmos/CosmosPaymentStores.cs).
**Walkthrough:** [04](../walkthroughs/04-shipping-delivery-invoice.md).

### Claim check and file outbox

**Problem:** writing a file and publishing a message cannot share a transaction; large payloads do not belong in
messages.
**In ShopFlow:** the invoice PDF goes to Blob Storage (`If-None-Match: *`, metadata); Event Grid turns `BlobCreated`
into a Service Bus message; registration publishes the small `InvoiceIssued` event that points at the file.
**Code:** [InvoicingServices.cs](../../src/Services/Payments/ShopFlow.Payments.Application/Invoicing/InvoicingServices.cs),
[InvoiceBlobEventsFunction.cs](../../src/Services/Payments/ShopFlow.Payments.Functions/Functions/InvoiceBlobEventsFunction.cs),
[eventgrid-invoices.bicep](../../infra/modules/messaging/eventgrid-invoices.bicep).
**Walkthrough:** [04](../walkthroughs/04-shipping-delivery-invoice.md).

### Exactly-once effect with a marker

**Problem:** Event Hubs delivers at least once; revenue must not be counted twice.
**In ShopFlow:** a marker document (event id) and the counter increment are written in one `TransactionalBatch`; a
duplicate conflicts on the marker and the whole batch rolls back.
**Code:** `ApplyOnceAsync` in [CosmosMetricStore.cs](../../src/Services/Analytics/ShopFlow.Analytics.Infrastructure/CosmosMetricStore.cs).
**Walkthrough:** [08](../walkthroughs/08-clickstream-analytics.md).

### Event streaming and partitioning

**Problem:** thousands of click events per second; replay and multiple independent readers matter more than
per-message settlement.
**In ShopFlow:** Event Hubs with a partition key per stream (`sessionId`, `orderId`), consumer groups with independent
checkpoints, Capture to ADLS Gen2 as the cold path, a batched forwarder from Service Bus.
**Code:** [EventHubsPublishers.cs](../../src/Services/Analytics/ShopFlow.Analytics.Infrastructure/EventHubsPublishers.cs),
[AggregationFunctions.cs](../../src/Services/Analytics/ShopFlow.Analytics.Functions/AggregationFunctions.cs).
[ADR-0005](../adr/0005-service-bus-event-hubs-event-grid.md). **Walkthrough:** [08](../walkthroughs/08-clickstream-analytics.md).

### Idempotent side effects outside the database

**Problem:** sending an e-mail cannot be rolled back or deduplicated by a database transaction.
**In ShopFlow:** a notification log: reserve the send, call ACS with a stable operation id, record the result; a retry
checks the previous attempt. SMS is deliberately "at most once".
**Code:** [NotificationSender.cs](../../src/Services/Notifications/ShopFlow.Notifications.Application/NotificationSender.cs),
[CommunicationChannels.cs](../../src/Services/Notifications/ShopFlow.Notifications.Infrastructure/Channels/CommunicationChannels.cs).
**Walkthrough:** [04](../walkthroughs/04-shipping-delivery-invoice.md).

---

## Integration and edge

### Gateway offloading (partner authentication)

**Problem:** every service re-implementing partner token checks, IP filters and rate limits.
**In ShopFlow:** Front Door (WAF) and APIM do TLS, JWT validation, rate limits, IP filters and partner token checks,
then call backends with APIM's own managed identity token; Easy Auth on Functions admits only that identity.
**Code:** [infra/apim/policies](../../infra/apim/policies), [EasyAuthPrincipal.cs](../../src/BuildingBlocks/ShopFlow.Functions/EasyAuthPrincipal.cs).
**Walkthrough:** [04](../walkthroughs/04-shipping-delivery-invoice.md), [08](../walkthroughs/08-clickstream-analytics.md).

### Secure webhook intake

**Problem:** a webhook is an unauthenticated POST from the internet that can be forged or replayed.
**In ShopFlow:** HMAC signature over the body checked against two secrets (rotation without downtime), and the webhook
treated as an incoming message for the inbox, so a repeated delivery has no effect.
**Code:** [PspWebhooks.cs](../../src/Services/Payments/ShopFlow.Payments.Infrastructure/Psp/PspWebhooks.cs),
[PspWebhookProcessor.cs](../../src/Services/Payments/ShopFlow.Payments.Application/Webhooks/PspWebhookProcessor.cs).
**Runbook:** [secret-rotation.md](../runbooks/secret-rotation.md). **Walkthrough:** [02](../walkthroughs/02-payment-declined-compensation.md).

### Anti-corruption layer for external providers

**Problem:** the PSP's and the carrier's models and error codes must not leak into the domain.
**In ShopFlow:** gateway clients translate provider requests, responses and errors into domain types behind ports
(`IPaymentGateway`, the carrier gateway), including error classification (retryable or not).
**Code:** [PspGatewayClient.cs](../../src/Services/Payments/ShopFlow.Payments.Infrastructure/Psp/PspGatewayClient.cs),
[ShippingInfrastructure.cs](../../src/Services/Shipping/ShopFlow.Shipping.Infrastructure/ShippingInfrastructure.cs).

### Valet key (user delegation SAS)

**Problem:** customers and warehouse staff need files from a storage account that has no public endpoint and no
account keys.
**In ShopFlow:** short-lived user delegation SAS links (signed with a key obtained by the managed identity) on the
Front Door files host; the API never streams the file.
**Code:** [BlobReadLinkFactory.cs](../../src/BuildingBlocks/ShopFlow.Azure/Storage/BlobReadLinkFactory.cs),
`shipment-label` in [ShippingFunctions.cs](../../src/Services/Shipping/ShopFlow.Shipping.Functions/ShippingFunctions.cs).
**Walkthrough:** [04](../walkthroughs/04-shipping-delivery-invoice.md).

### Resilience pipeline (retry, circuit breaker, timeout)

**Problem:** a slow dependency exhausts threads and turns a partial outage into a total one.
**In ShopFlow:** the standard HTTP resilience handler (rate limiter, total timeout, retry with jitter honouring
`Retry-After`, circuit breaker, attempt timeout) with per-client tuning; a stale-price fallback for the catalog.
**Code:** [HttpResilienceExtensions.cs](../../src/BuildingBlocks/ShopFlow.Resilience/HttpResilienceExtensions.cs),
[CatalogPricingClient.cs](../../src/Services/Orders/ShopFlow.Orders.Infrastructure/Catalog/CatalogPricingClient.cs).

### API versioning with Deprecation and Sunset

**Problem:** public clients cannot all upgrade on the day an API changes.
**In ShopFlow:** versions in the path; a retiring version answers with `Deprecation`, `Sunset` and a successor link.
**Code:** [PublicOrderEndpoints.cs](../../src/Services/Orders/ShopFlow.Orders.Api/Endpoints/PublicOrderEndpoints.cs).
**Walkthrough:** [10](../walkthroughs/10-contract-v2-rollout.md).

---

## Security and operations

### Managed identity with zero keys

**Problem:** connection strings and keys leak, expire and are shared too widely.
**In ShopFlow:** user-assigned managed identities for every unit, local auth disabled on every service, roles at the
narrowest scope (a queue, a container, a database); configuration holds only endpoints and client ids.
**Code:** [AzureCredentialFactory.cs](../../src/BuildingBlocks/ShopFlow.Azure/Identity/AzureCredentialFactory.cs),
[data.bicep](../../infra/layers/data.bicep). **Docs:** [identity-rbac.md](identity-rbac.md).
[ADR-0010](../adr/0010-managed-identities-and-app-roles.md).

### Workload identity federation (OIDC)

**Problem:** CI/CD secrets for Azure are the most valuable secrets in the organization.
**In ShopFlow:** GitHub OIDC tokens with a precise subject (branch, pull request, environment) are exchanged for
managed identity tokens; no Azure secret exists in GitHub; Notifications reads another tenant's Graph the same way.
**Code:** [infra/bootstrap](../../infra/bootstrap), [azure-login](../../.github/actions/azure-login/action.yml),
[GraphCustomerDirectory.cs](../../src/Services/Notifications/ShopFlow.Notifications.Infrastructure/Customers/GraphCustomerDirectory.cs).
**Walkthrough:** [09](../walkthroughs/09-release-pipeline.md).

### Network isolation with private endpoints

**Problem:** data services reachable from the internet are one misconfigured firewall rule away from a breach.
**In ShopFlow:** one VNet per environment, private endpoints and private DNS for every data service and broker,
public access disabled; documented exceptions with compensating controls.
**Docs:** [network.md](network.md), [ADR-0011](../adr/0011-network-topology.md).

### Feature flags

**Problem:** deploying code and releasing behavior are different decisions.
**In ShopFlow:** App Configuration feature flags refreshed at runtime: dual publishing of contract versions, pickup-point
delivery, forced 3-D Secure, SMS for a percentage of customers (targeting by customer id).
**Code:** [OrderFeatures.cs](../../src/Services/Orders/ShopFlow.Orders.Infrastructure/Support/OrderFeatures.cs),
[app-configuration.bicep](../../infra/modules/config/app-configuration.bicep).
**Walkthrough:** [10](../walkthroughs/10-contract-v2-rollout.md).

### Health endpoints

**Problem:** the platform must know when an instance can take traffic and when it must be restarted.
**In ShopFlow:** three endpoints: `/health/live` (the process responds), `/health/ready` (startup finished and not
shutting down; used for slot swap warm-up and readiness probes) and `/health/dependencies` (diagnostics only).
Readiness deliberately ignores dependencies: a Cosmos DB outage must not make the platform recycle every instance.
**Code:** [HealthEndpoints.cs](../../src/BuildingBlocks/ShopFlow.Web/Health/HealthEndpoints.cs),
[LifecycleHealthCheck.cs](../../src/BuildingBlocks/ShopFlow.Observability/LifecycleHealthCheck.cs).

### Queue-based load leveling

**Problem:** traffic spikes (campaigns) would overload the database if every request did all the work synchronously.
**In ShopFlow:** the API accepts the order (202) and queues the work; consumers scale on queue length and their
concurrency is sized for the downstream limit (e.g. `maxConcurrentCalls: 8` for the carrier API).
**Code:** [ShopFlow.Shipping.Functions README](../../src/Services/Shipping/ShopFlow.Shipping.Functions/README.md),
[Worker/Program.cs](../../src/Services/Inventory/ShopFlow.Inventory.Worker/Program.cs).

---

## Delivery

### Build once, deploy many

**Problem:** rebuilding per environment means prod runs something that was never tested.
**In ShopFlow:** one build produces zip packages and SHA-tagged, scanned, attested images; dev, test and prod deploy
the same artifacts.
**Code:** [_build.yml](../../.github/workflows/_build.yml), [_deploy.yml](../../.github/workflows/_deploy.yml).
**Walkthrough:** [09](../walkthroughs/09-release-pipeline.md).

### Deployment strategies: slots, revisions, rolling update

**Problem:** zero-downtime deployment means different things on different hosts.
**In ShopFlow:** App Service slot swap (instant rollback), Container Apps revisions, Functions Flex rolling update
(old and new code side by side, hence compatible contracts and orchestration versioning).
[ADR-0014](../adr/0014-deployment-strategies.md). **Walkthrough:** [09](../walkthroughs/09-release-pipeline.md).

### Expand-contract schema migrations

**Problem:** during a deployment the old code runs against the new schema.
**In ShopFlow:** migrations run as a job before the new code and only expand (add); removals happen in a later release.
Permissions for application identities are granted in a migration.
**Code:** [Migrations](../../src/Services/Inventory/ShopFlow.Inventory.Infrastructure/Persistence/Migrations),
[run-migrations](../../.github/actions/run-migrations/action.yml). **Skill:** `/ef-migration`.

### Consumer-driven contracts (Pact)

**Problem:** a provider change that breaks a consumer is found only in integration environments.
**In ShopFlow:** consumer tests generate pacts (HTTP and messages); provider tests replay them against the real code;
CI fails when a consumer changes a pact without committing it.
**Code:** [tests/ShopFlow.Pact.Tests](../../tests/ShopFlow.Pact.Tests/README.md).
**Walkthrough:** [10](../walkthroughs/10-contract-v2-rollout.md).

### Layered infrastructure as code

**Problem:** one huge template mixes rarely changed, privileged resources with daily application changes.
**In ShopFlow:** an administrator-only bootstrap (identities, roles, groups, budgets) and a pipeline-deployed `main.bicep`
in six layers; types per environment; names and tags from functions.
**Code:** [infra/README.md](../../infra/README.md). [ADR-0002](../adr/0002-bicep-over-terraform.md),
[ADR-0015](../adr/0015-environment-parity-and-cost.md).
