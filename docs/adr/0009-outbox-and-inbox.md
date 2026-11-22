# ADR-0009: Two outbox variants and an inbox

| Status | Date | Deciders |
|---|---|---|
| Accepted | 2026-11-14 | architecture |

## Context

Saving state and publishing a message involve two different systems (a database and Service Bus). Without a shared
transaction, "save, then send" loses the event on a crash between the steps, and "send, then save" publishes an event
about a change that does not exist. Every broker in Azure delivers at least once, so a consumer will also see duplicates.

## Decision

An **outbox** wherever a service publishes, in two variants matching the store:

| Variant | Where | Write | Publishing |
|---|---|---|---|
| Cosmos DB | Orders, Payments, Shipping | an outbox record in the aggregate's partition, one `TransactionalBatch` | a function with a Change Feed trigger (`CosmosOutboxRelay`) |
| Azure SQL | Inventory | the `messaging.OutboxMessages` table in the same EF Core transaction | right after the commit (`OutboxSignal` → `SqlOutboxRelay`), a poller closes the gaps |

An **inbox** for every consumer: the identifier of a processed message saved in the same transaction as the state
change. `MessageId` = the outbox record identifier, so a republish after a relay crash also falls into the broker's
duplicate detection window (10 min).

Exception: Notifications has no inbox, because its effect is an e-mail, not a database write. The send history has three
states (reservation → send → outcome), and a reservation found later means the provider must first be asked about the
operation's status.

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **Outbox + inbox** (chosen) | no lost or phantom events; duplicates neutralized | extra documents and RU; publishing latency (seconds) |
| Dual write (save, then `SendAsync`) | simple | events lost on a process crash; indefensible for payments |
| Cosmos outbox with polling | one pattern for both databases | queries every few seconds cost RU even without traffic; the Change Feed costs nothing in RU for reading the feed |
| CDC for SQL (Debezium, Change Tracking) | no relay code | extra infrastructure (Kafka Connect) and database permissions |
| Change Feed "all versions and deletes" instead of separate documents | fewer writes | requires continuous backup and has a retention window; complicates the relay |

## Consequences

- **Outbox lag** is the main health indicator of publishing: the `shopflow.outbox.lag` metric and the `outbox-lag`
  alert (> 2 min).
- **Ordering** is preserved within a Change Feed partition (one order); across orders it is not needed. Session-enabled
  queues on the consumer side keep it further.
- **A malformed outbox document** is skipped and logged as `Critical` (the `outbox-malformed` alert) so that it does not
  stop the Change Feed partition forever.
- **TTL of 7 days** for outbox and inbox records in Cosmos: the inbox protects against duplicates older than the broker
  window, but not for longer than a week. Replaying older messages from the DLQ is a deliberate operator decision.
- Two variants mean more code, but they show that the pattern does not depend on the store.

## In the repository

[`ShopFlow.Persistence.Cosmos`](../../src/BuildingBlocks/ShopFlow.Persistence.Cosmos) (`CosmosUnitOfWork`, `CosmosOutboxRelay`, `CosmosInbox`),
[`ShopFlow.Persistence.SqlServer`](../../src/BuildingBlocks/ShopFlow.Persistence.SqlServer) (`SqlOutboxRelay`, `SqlOutboxPollerService`),
[`ServiceBusMessagePublisher`](../../src/BuildingBlocks/ShopFlow.Messaging.ServiceBus/ServiceBusMessagePublisher.cs).
