# 10. Rolling out a breaking contract change: `OrderSubmitted` v2

The business wants pickup-point delivery. `OrderSubmitted` v1 has a mandatory `ShippingAddress`; a pickup-point order
has no address, so the change cannot be additive. Publishers and consumers deploy independently, and old messages may
wait in queues and DLQs for days. This walkthrough shows how ShopFlow moves from v1 to v2 without a big-bang deployment.

Patterns: [versioned message contracts](../architecture/patterns.md#versioned-contracts-and-upcasting),
[parallel publishing behind a feature flag](../architecture/patterns.md#feature-flags),
[consumer-driven contracts](../architecture/patterns.md#consumer-driven-contracts-pact),
[API versioning](../architecture/patterns.md#api-versioning-with-deprecation-and-sunset).
Decision: [ADR-0012](../adr/0012-contract-versioning.md).

## The two versions

| | v1 [`Orders/V1`](../../src/BuildingBlocks/ShopFlow.Contracts/Orders/V1) | v2 [`Orders/V2/OrderSubmitted.cs`](../../src/BuildingBlocks/ShopFlow.Contracts/Orders/V2/OrderSubmitted.cs) |
|---|---|---|
| Delivery | `ShippingAddress` (required) | `Delivery { Method, Address?, PickupPointId? }` |
| Contract id | `[MessageContract("OrderSubmitted", 1)]` | `[MessageContract("OrderSubmitted", 2)]` |
| Broker properties | `Subject = OrderSubmitted`, `ContractVersion = 1` | `Subject = OrderSubmitted`, `ContractVersion = 2` |

Both types live side by side in `ShopFlow.Contracts`; the registry maps (name, version) to the CLR type, so a consumer
deserializes exactly the version it receives ([MessageTypeRegistry.cs](../../src/BuildingBlocks/ShopFlow.Contracts/MessageTypeRegistry.cs)).

## Phases

| Phase | What changes | Where it executes | Code / config |
|---|---|---|---|
| **1. Add v2** | New contract type, schema snapshot `OrderSubmitted.v2.approved.json`, upcaster v1 → v2. Nothing is published yet. Contract tests prove both versions are registered under one name and the upcaster loses no data. | build only | [OrderSubmittedV1ToV2Upcaster.cs](../../src/BuildingBlocks/ShopFlow.Contracts/Orders/OrderSubmittedV1ToV2Upcaster.cs), [ContractCompatibilityTests.cs](../../tests/ShopFlow.Contracts.Tests/ContractCompatibilityTests.cs) |
| **2. Publish both** | Orders always enqueues v2 and, while the flag `Orders.PublishOrderSubmittedV1` is on, also v1 (courier orders only) in the **same outbox batch**. Pickup point stays blocked by `Orders.PickupPointDelivery = off`, because such an order cannot be expressed as v1. | `orders-api` (outbox), `orders-processors` (relay) | [OrderEventPublisher.cs](../../src/Services/Orders/ShopFlow.Orders.Application/Orders/OrderEventPublisher.cs), [SubmitOrder.cs](../../src/Services/Orders/ShopFlow.Orders.Application/Orders/SubmitOrder.cs), [app-configuration.bicep](../../infra/modules/config/app-configuration.bicep) (feature flags) |
| **3. Move consumers** | Each consumer picks **one** version, so nobody processes the same order twice: the saga's `saga-start` subscription filters `ContractVersion = '2'`; the read model registers a handler for v2 only; analytics counts only v2. The `readmodels` and `analytics` subscriptions receive both versions and ignore v1. | `order-saga`, `readmodels-projections`, `analytics-processor` | [messaging-topology.json](../../infra/modules/messaging/messaging-topology.json) (`saga-start` filter), [ProjectionHandlers.cs](../../src/Services/CustomerReadModels/ShopFlow.CustomerReadModels.Application/ProjectionHandlers.cs), [Metrics.cs](../../src/Services/Analytics/ShopFlow.Analytics.Application/Metrics.cs) |
| **4. Watch** | Query 4 in [messaging.kql](../kql/messaging.kql) lists who still processes contract version 1 (the version is a span attribute). When it is empty for a full DLQ retention period, v1 has no consumers left. | Log Analytics | — |
| **5. Stop v1** | Turn off `Orders.PublishOrderSubmittedV1` in App Configuration (no deployment; the flag refreshes at runtime). Then turn on `Orders.PickupPointDelivery`: the saga now sends `CreateShipment` **v2** for pickup-point orders, and Shipping handles v1 and v2 with one handler. | App Configuration, `order-saga`, `shipping-processor` | [SagaCommandFactory.cs](../../src/Services/OrderSaga/ShopFlow.OrderSaga/Model/SagaCommandFactory.cs) `CreateShipment`, [ShipmentHandlers.cs](../../src/Services/Shipping/ShopFlow.Shipping.Application/ShipmentHandlers.cs) |
| **6. Remove v1** | Delete the v1 publishing code; keep the v1 type and the upcaster while old messages can still be replayed from a DLQ or an archive (the upcaster's only job). Remove them in a later release. | build only | [IMessageUpcaster.cs](../../src/BuildingBlocks/ShopFlow.Contracts/IMessageUpcaster.cs) |

## Why each consumer chooses exactly one version

During phase 2 both versions of the same order are on the topic. If the saga subscribed to both, it would try to start
twice (the instance id `order-{orderId}` would save it, but only by luck); if the read model counted both, it would
apply the header twice. The rule "one consumer, one version" is enforced by filters and handler registrations, and it
is visible in the topology file, not hidden in code.

## The same idea for HTTP

| | `/v1/orders` | `/v2/orders` |
|---|---|---|
| Body | courier address only | delivery method object |
| Idempotency | `Idempotency-Key` required | `Idempotency-Key` required |
| Lifecycle | `Deprecation` and `Sunset` headers on every response, link to the successor version | current |

[PublicOrderEndpoints.cs](../../src/Services/Orders/ShopFlow.Orders.Api/Endpoints/PublicOrderEndpoints.cs)
(`AnnounceDeprecation`). Clients see the retirement date in every response long before v1 disappears.

## Publishing the contracts package

Other repositories (if there were any) consume `ShopFlow.Contracts` as a NuGet package. A tag `contracts/v*` runs
[contracts-publish.yml](../../.github/workflows/contracts-publish.yml), which validates the package against the
previous version (API compatibility: no removed types or changed signatures) before publishing to the organization
feed. In this monorepo the snapshot tests and Pact cover the wire format; package validation covers the C# surface.

## Try it locally

```powershell
dotnet test --project tests/ShopFlow.Contracts.Tests
```

```powershell
dotnet test --project tests/ShopFlow.Pact.Tests --filter-namespace "ShopFlow.Pact.Tests.Consumers"
```

To practise: add an optional field to a v1 contract and watch only the snapshot test fail (approve it with
`SHOPFLOW_ACCEPT_SNAPSHOTS=1`); then make it required and see why that needs a v2. The `/add-message-contract` skill
and the `contract-change-reviewer` agent guide the same steps in Claude Code.
