# ADR-0005: Service Bus, Event Hubs or Event Grid

| Status | Date | Deciders |
|---|---|---|
| Accepted | 2026-11-14 | architecture |

## Context

The system has three kinds of asynchronous traffic: commands and domain events (must not be lost, need a DLQ, ordering
per order, deduplication), a click stream (high volume, throughput and replay matter, losing a single event is
acceptable) and notifications about changes to Azure resources (a new invoice file in Blob Storage).

## Decision

| Need | Service |
|---|---|
| Commands and domain events | **Service Bus Premium**: queues for commands, a topic per publishing context, `orderId` sessions, 10-minute deduplication |
| Business telemetry stream | **Event Hubs Standard**: the `clickstream` and `business-events` hubs, Capture to ADLS Gen2 |
| Azure resource events | **Event Grid** system topic on the `docs` account, delivery to a Service Bus queue |

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **Three brokers by need** (chosen) | each service does what it was designed for | three SDKs, three error models, three sets of metrics |
| Service Bus for everything | one model | clickstream expensive per message and not replayable; Blob does not publish to Service Bus by itself |
| Event Hubs for everything (Kafka-style) | one log, replay | no DLQ and no per-message acknowledgement; ordering only within a partition; the consumer manages checkpoints and retries itself |
| Event Grid for domain events (custom topics) | push, filtering, cheap | no sessions or ordering; push to public endpoints (ours are private); limited DLQ |
| Service Bus Standard | ~10× cheaper in dev | no private endpoints or resource isolation; a different network path than in prod ([ADR-0015](0015-environment-parity-and-cost.md)) |

## Consequences

- **Event Grid does not push to private endpoints**, so `BlobCreated` goes to the `invoice-blob-events` queue, and the
  Service Bus namespace has the "trusted services" exception ([network.md](../architecture/network.md)). A side benefit:
  a DLQ and retries on the consumer side.
- **Blob + Event Grid as an outbox for files:** writing a file and publishing a message cannot be covered by one
  transaction; `BlobCreated` is emitted after the durable write.
- **The clickstream is approximate:** an exception in a function with an Event Hubs trigger does not roll back the
  checkpoint; after the retries are exhausted the batch remains only in the Capture archive. Business metrics are exact
  thanks to event markers.
- **The fixed cost of Premium** (one MU per environment) is the most expensive messaging item ([cost.md](../cost.md)).

## In the repository

[messaging-topology.json](../../infra/modules/messaging/messaging-topology.json), [servicebus.bicep](../../infra/modules/messaging/servicebus.bicep),
[eventhubs.bicep](../../infra/modules/messaging/eventhubs.bicep), [eventgrid-invoices.bicep](../../infra/modules/messaging/eventgrid-invoices.bicep),
[message-flows.md](../architecture/message-flows.md).
