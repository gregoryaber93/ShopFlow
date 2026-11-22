# 05. A message keeps failing: retries, the DLQ and replay

Two failures, two different paths. In the first, Azure Communication Services is down for 20 minutes and
notification messages fail temporarily. In the second, a `ShipmentDelivered` event arrives for an order that was
cancelled, which no retry can fix. Both end in a dead-letter queue (DLQ); only one should be replayed.

Patterns: [retry vs. dead letter](../architecture/patterns.md#retry-vs-dead-letter),
[idempotent receiver](../architecture/patterns.md#idempotent-receiver),
[dead letter channel and replay](../architecture/patterns.md#dead-letter-channel-and-replay),
[topology as data](../architecture/patterns.md#topology-as-data).

## Where the decision is made

Every Service Bus consumer, whether a Functions trigger or the Container Apps worker, goes through the same pipeline:

```text
ServiceBusTriggerAdapter / ServiceBusQueueConsumer
  └─ ServiceBusMessageProcessor        envelope → contract type (registry) → upcaster → dispatcher
       └─ MessageDispatcher            inbox check → IMessageHandler<T>.HandleAsync
            exception? → MessageFailureClassifier
                 Permanent → DeadLetterAsync(reason, description)      (immediately)
                 Transient → hold the lock with backoff, then rethrow   (redelivery, DeliveryCount + 1)
            no exception → CompleteAsync
```

| Code | Role |
|---|---|
| [ServiceBusMessageProcessor.cs](../../src/BuildingBlocks/ShopFlow.Messaging.ServiceBus/ServiceBusMessageProcessor.cs) | settles the message: complete, dead-letter, or rethrow after a growing delay |
| [MessageFailureClassifier.cs](../../src/BuildingBlocks/ShopFlow.Messaging/Failures/MessageFailureClassifier.cs) | walks the exception chain through pluggable classifiers: the generic rules (`JsonException`, `DomainException`, `PermanentMessageException` → permanent; `ConcurrencyConflictException`, `OutOfOrderMessageException`, timeouts → transient) plus adapter rules registered by the Cosmos, SQL and Service Bus libraries (e.g. 429/503, SQL error numbers); anything unknown → transient |
| [MessageDispatcher.cs](../../src/BuildingBlocks/ShopFlow.Messaging/Handling/MessageDispatcher.cs) | inbox: the inbox entry is saved in the same transaction as the handler's state change; a message id processed before makes that save fail with `DuplicateMessageException`, and the message completes as a duplicate with no effect |
| [messaging-topology.json](../../infra/modules/messaging/messaging-topology.json) | `maxDeliveryCount`: 5 for command queues, 10 for inbox queues and subscriptions; duplicate detection window 10 min |

## Case A: a transient failure that outlives the retries

| # | Where | What happens | Data / signal |
|---|---|---|---|
| 1 | `notifications-processor` | `OrderConfirmed` arrives; sending the e-mail through ACS throws (503). Classified **transient**. | log "Transient failure (…) will be redelivered" |
| 2 | same | Service Bus has no delayed redelivery: an abandoned message is available again at once. So the processor **holds the lock** for a growing delay before rethrowing; sleeping invocations also slow down fetching. | `DeliveryCount` 1 → 2 → … |
| 3 | Service Bus | After `maxDeliveryCount` (10 for `notifications-inbox`) the **broker** moves the message to the DLQ with reason `MaxDeliveryCountExceeded`. The application does not see this moment. | `notifications-inbox/$DeadLetterQueue` |
| 4 | Azure Monitor | Metric alert `sb-deadletters` (dead-lettered messages per entity) fires; severity 2. | [alerts.bicep](../../infra/modules/operations/alerts.bicep) |
| 5 | On-call | Runbook [dlq-growth.md](../runbooks/dlq-growth.md): which entity, how many, which reason; [dead-letters.kql](../kql/dead-letters.kql) query 3 shows the attempts before the DLQ, query 5 the transient cause (ACS 503). | Log Analytics |
| 6 | On-call (after ACS recovers) | `shopflow-dlq peek`, then `replay --dry-run`, then `replay` with a `--reason` filter. Run from inside the VNet (Bastion or a Container Apps job) with time-limited PIM roles. | [tools/ShopFlow.DlqReplay](../../tools/ShopFlow.DlqReplay/README.md) |
| 7 | `shopflow-dlq` | Keeps the original `MessageId` (so the consumer's inbox recognizes a partial earlier success), skips messages younger than the duplicate detection window, refuses a message replayed 3 times already, **sends first, deletes from the DLQ second** (a crash in between gives a duplicate, never a loss). | [ReplayPlanning.cs](../../tools/ShopFlow.DlqReplay/ReplayPlanning.cs), [DeadLetterTool.cs](../../tools/ShopFlow.DlqReplay/DeadLetterTool.cs) |
| 8 | `notifications-processor` | The replayed message is processed; the notification log makes the e-mail send idempotent, so a customer who already got the mail before the failure does not get a second one. | [NotificationSender.cs](../../src/Services/Notifications/ShopFlow.Notifications.Application/NotificationSender.cs) |

### Where does a replayed message go?

The tool computes the target from the embedded topology:

| DLQ of | Replay target | Why |
|---|---|---|
| a queue (`notifications-inbox`) | the same queue | simplest case |
| a subscription with auto-forward (`orders-events/notifications`) | the forward target queue | the message got stuck on the forward hop |
| a subscription read directly (`orders-events/saga-start`, `payments-events/invoicing`, `shipping-events/orders`) | the topic, with the property `ReplayTarget = <subscription>` | you cannot send to a subscription; Bicep gives every rule on such a topic `ReplayTarget IS NULL` and the target subscription an extra `ReplayTarget = '<name>'` rule, so only it receives the copy |

## Case B: a permanent failure

| # | Where | What happens | Data / signal |
|---|---|---|---|
| 1 | `orders-processors` (`shipment-events`) | `ShipmentDelivered` for an order in `Cancelled`. The domain returns `order.closed`; the handler throws `PermanentMessageException(BusinessRuleViolation)`. | [ShipmentEventHandlers.cs](../../src/Services/Orders/ShopFlow.Orders.Application/Shipping/ShipmentEventHandlers.cs) `ShipmentEventSupport` |
| 2 | `ServiceBusMessageProcessor` | Dead-letters **immediately** (no point burning 10 attempts) with reason `BusinessRuleViolation` and a description; metric `shopflow.messaging.dead_lettered` + a warning log. | `shipping-events/orders/$DeadLetterQueue` |
| 3 | On-call | The same alert; [dead-letters.kql](../kql/dead-letters.kql) query 1 groups by consumer and reason. This is a **business** problem: a parcel left for a cancelled order. The message is **not** replayed; it is evidence for the investigation (carrier, refund, warehouse). | — |

Other permanent reasons: `MalformedEnvelope`, `DeserializationFailed`, `UnhandledMessageType` (a filter
misconfiguration with the `DeadLetter` policy), `ValidationFailed`. A code fix followed by a replay is the normal
remedy for the first three.

## Design notes

- **Why hold the lock instead of scheduling a delayed copy?** A scheduled copy breaks ordering within a session and
  does not work for subscriptions read directly; holding the lock is the simplest backoff that keeps both.
- **Why replay is manual:** replaying before the cause is fixed only refills the DLQ. The tool forces a deliberate
  choice (filters, dry run, replay count).
- **Why idempotency matters here:** replay is "at least once" by construction. Every handler must survive seeing the
  same `MessageId` again ([01](01-place-order.md), [ADR-0009](../adr/0009-outbox-and-inbox.md)).

## Try it locally

```powershell
dotnet test --project src/BuildingBlocks/tests/ShopFlow.BuildingBlocks.Tests --filter-class "*ServiceBusMessageProcessorTests" --filter-class "*FailureClassificationTests"
```

```powershell
dotnet test --project tools/ShopFlow.DlqReplay.Tests
```

The `incident-responder` agent in Claude Code can run this scenario as a tabletop exercise.
