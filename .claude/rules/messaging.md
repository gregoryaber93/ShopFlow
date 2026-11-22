---
paths:
  - "src/BuildingBlocks/ShopFlow.Messaging/**"
  - "src/BuildingBlocks/ShopFlow.Messaging.ServiceBus/**"
  - "src/BuildingBlocks/ShopFlow.Functions/**"
  - "src/BuildingBlocks/ShopFlow.Persistence.*/**/*Outbox*"
  - "src/BuildingBlocks/ShopFlow.Persistence.*/**/*Inbox*"
  - "src/Services/**/*Handler*.cs"
  - "src/Services/**/*Functions*.cs"
  - "src/Services/**/*Publisher*.cs"
  - "src/Services/**/Program.cs"
  - "infra/modules/messaging/**"
  - "tools/ShopFlow.DlqReplay/**"
---

# Messaging rules (Service Bus, Event Hubs, Event Grid)

Read [docs/architecture/message-flows.md](../../docs/architecture/message-flows.md) and
[ADR-0009](../../docs/adr/0009-outbox-and-inbox.md) before changing anything here.

## Publishing

- Never call a Service Bus sender from business code. Business code writes to `IOutbox`; the unit of work commits the
  state change and the outbox record together (Cosmos `TransactionalBatch` in the aggregate's partition, or one EF Core
  transaction in SQL). A relay publishes later (Change Feed trigger or SQL poller).
- `MessageId` = the outbox record id, so a republish after a relay crash is caught by broker duplicate detection.
- Every message carries `CorrelationId`, `CausationId` and the W3C trace context (`MessageEnvelope`).
- Ordered messages per order use `SessionId = orderId` (`IOrderScoped`); queues that need ordering have
  `requiresSession: true` in the topology.

## Consuming

- A handler implements `IMessageHandler<TMessage>` for one contract version, is `sealed`, and is **idempotent**:
  the same message can arrive more than once (at-least-once delivery, replays from the DLQ).
  Use the inbox (`IInbox`) or natural idempotency (state checks, ETags, deterministic ids).
- Register handlers in the host's `Program.cs` with `.AddHandler<TMessage, THandler>()`; one handler may serve two
  versions (e.g. `CreateShipment` v1 and v2).
- Function triggers set `AutoCompleteMessages = false` and delegate to `ServiceBusTriggerAdapter.ProcessAsync` with a
  `ConsumerEndpoint` (consumer name from `MessagingTopology.Consumers`, unhandled-message policy).
- Failure classification decides retry vs. DLQ (`MessageFailureClassifier`): transient exceptions are rethrown
  (redelivery), permanent ones dead-letter with a reason. Do not catch-and-log to complete a poison message.
- A message that arrives before the state it refers to throws `OutOfOrderMessageException` (classified as transient,
  so it is redelivered) or is absorbed by facts-based state (see `CustomerOrderView`). Do not rely on arrival order
  across sessions or topics.

## Topology is data

- Every queue, topic, subscription, rule, sender and receiver is declared in
  `infra/modules/messaging/messaging-topology.json` and mirrored by constants in
  `src/BuildingBlocks/ShopFlow.Contracts/MessagingTopology.cs`. `MessagingTopologyTests` and `RoutingTopologyTests`
  fail when they diverge. Bicep reads the JSON; never hard-code an entity name in a `.bicep` file.
- Subscriptions read directly (not auto-forwarded) get two rules so `shopflow-dlq replay` can target them: the normal
  filter extended with `ReplayTarget IS NULL`, and a second rule `ReplayTarget = '<subscription>'`
  (generated in `servicebus.bicep` from the topology).
- Roles are per entity: a sender gets Data Sender on its topic/queue only, a consumer Data Receiver on its queue or
  subscription only.

Use the `/add-message-consumer` skill for a new consumer and `/add-message-contract` for a new message.
