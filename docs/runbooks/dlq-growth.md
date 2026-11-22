# Runbook: messages in a DLQ, queue backlogs, the outbox

Alerts: `sb-deadletters` (any message in a DLQ), `sb-backlog` (backlog > 1000 for 15 min), `outbox-lag`,
`outbox-malformed`, `projection-lag`. Tool: [`shopflow-dlq`](../../tools/ShopFlow.DlqReplay/README.md).

## Symptoms and impact

| Entity with a DLQ | Customer impact |
|---|---|
| `inventory-commands`, `payments-commands`, `shipping-commands`, `saga-replies` | the saga waits for a reply until the step timeout, then compensates: **the order will be rejected** |
| `orders-events/saga-start` | the saga for the order did not start: **the order hangs in the `Submitted` state** |
| `notifications-inbox` | no e-mail or SMS; the order works |
| `readmodels-inbox` | "my orders" shows a stale state |
| `analytics-inbox` | a gap in the business metrics |
| `invoice-blob-events`, `payments-events/invoicing` | no invoice or no invoice e-mail |
| `shipping-events/orders` | the order does not move to `Shipped` / `Completed` |

Every message in a DLQ is a specific order that nobody handled. A DLQ does not "grow normally".

## Diagnosis

### 1. Which entity and how many

The `DeadletteredMessages` metric has an `EntityName` dimension at the queue or topic level. For a topic you need to
check the subscriptions.

```bash
az servicebus queue list -g $RG_DATA --namespace-name $SB \
  --query "[?countDetails.deadLetterMessageCount > \`0\`].{queue:name, dlq:countDetails.deadLetterMessageCount, active:countDetails.activeMessageCount}" -o table

for topic in orders-events inventory-events payments-events shipping-events; do
  az servicebus topic subscription list -g $RG_DATA --namespace-name $SB --topic-name $topic \
    --query "[?countDetails.deadLetterMessageCount > \`0\`].{sub:name, dlq:countDetails.deadLetterMessageCount}" -o table
done
```

### 2. Why: reason and content

From a machine in the VNet (the Data Receiver role activated in PIM):

```bash
export SHOPFLOW_SERVICEBUS_NAMESPACE=$SB.servicebus.windows.net
shopflow-dlq peek --queue payments-commands --max 20
shopflow-dlq peek --topic orders-events --subscription saga-start
```

`peek` shows the `MessageId`, `Subject`, enqueue time, delivery count, `DeadLetterReason` and the beginning of
`DeadLetterErrorDescription`. The processing trace in the logs: [dead-letters.kql](../kql/dead-letters.kql)
(by `MessageId`) and [order-trace.kql](../kql/order-trace.kql) (by `CorrelationId`).

### 3. Classification

| `DeadLetterReason` | Meaning | Action |
|---|---|---|
| `MaxDeliveryCountExceeded` | a transient error lasted longer than 5 (commands) or 10 attempts: a dependency was down, an RBAC role was missing, a timeout, a message too early (`OutOfOrderMessageException`) | remove the cause (dependency, role, deployment), then replay |
| `MalformedEnvelope`, `DeserializationFailed` | the publisher sent something the consumer cannot read | a code bug or a contract mismatch: fix and deploy, then replay |
| `UnhandledMessageType` | the consumer has no handler for the type (a new type, a wrong subscription rule) | fix the filter in the topology or add the handler; then replay |
| `ValidationFailed` | the payload breaks the contract's rules | usually a publisher bug; a business decision whether to replay |
| `BusinessRuleViolation` | the domain state does not allow the operation | a human decision, see below |

Check the causes of `MaxDeliveryCountExceeded` in this order:

1. **A deployment in the last hour?** Compare the `AppVersion` of the failures ([errors-by-service.kql](../kql/errors-by-service.kql)).
   A regression → [deployment-rollback.md](deployment-rollback.md).
2. **A dependency unavailable?** `AppDependencies` with `Success == false` for the consumer's role (Cosmos 429 →
   [cosmos-throttling.md](cosmos-throttling.md), PSP → [payment-failure.md](payment-failure.md)).
3. **403 from Service Bus, Cosmos or Storage?** The role was not granted or had not propagated after an infrastructure
   deployment. Compare with [identity-rbac.md](../architecture/identity-rbac.md).

## Actions

### Replay after removing the cause

```bash
# 1. What would be replayed (without sending)
shopflow-dlq replay --queue payments-commands --reason MaxDeliveryCountExceeded --dry-run

# 2. One message first; check in the logs that it went through
shopflow-dlq replay --queue payments-commands --message-id 0193a5c2-...

# 3. The rest
shopflow-dlq replay --queue payments-commands --reason MaxDeliveryCountExceeded --max 50
```

The tool keeps the `MessageId` (the consumer's inbox recognises a repeat), skips messages younger than the duplicate
detection window (10 min) and replays each message at most 3 times. `--force` disables both safeguards; use it only after
agreeing in the incident channel.

**Saga commands after a step timeout:** if the saga has already exceeded the timeout and compensated the order, replaying
`ReserveStock` or `AuthorizePayment` performs the operation after the fact. Compensations are state-tolerant, but check
the saga state (`GET /api/sagas/{orderId}`) before replaying commands. For `Rejected` or `Cancelled` orders replay only
compensations, never step commands.

### `reservation.not_active`

`OrderConfirmed` reached `inventory-commands`, but the reservation had already expired (`BusinessRuleViolation`; in the
`inventory-worker` log a Critical entry "was confirmed but its reservation is …: possible oversell"). The order is
confirmed, the payment captured, and the stock may have been sold to someone else.

1. Check the stock of the SKUs from the message in the reservation's warehouse (`GET /v1/catalog/products/{sku}` or a
   query against `inventory.StockItems` by someone with SQL access).
2. **The stock is there:** the warehouse team reserves it manually in the warehouse system; the DLQ message stays as a
   trace until the stock is corrected (a stock receipt with a correction through back-office).
3. **The stock is gone:** a business decision (back-order from the supplier or cancellation). A cancellation after
   confirmation is a refund in the PSP panel and a status change through back-office; customer support contacts the customer.
4. The systemic cause: the saga took longer than `Reservation:TimeToLive` (60 min). Check whether the saga was waiting
   for an operator or `orders-api` was unavailable ([saga-funnel.kql](../kql/saga-funnel.kql)).

`shopflow-dlq` has no command to delete a message (deletion is irreversible). A message handled manually is deleted in
Service Bus Explorer from a machine in the VNet, with a note in the ticket.

## Backlog without a DLQ (`sb-backlog` alert)

Messages do not reach the DLQ, but the consumer cannot keep up or has stopped.

| Consumer | What to check |
|---|---|
| `inventory-worker` (Container Apps) | the number of replicas and the KEDA rule: `az containerapp show -g $RG_APPS -n ca-shopflow-inventory-worker-$ENV-weu --query properties.template.scale`; the revision's logs; KEDA's access to the queue (the Data Owner role on `inventory-commands`) |
| function apps | `maximumInstanceCount` reached? Exceptions at host startup (`AppExceptions` for the role)? The `ServiceBus__*` settings after an infrastructure deployment? |
| `readmodels-projections` (`projection-lag` alert) | a session-enabled queue: one failing session blocks only its own order; many sessions at once is usually Cosmos 429 |

Ad-hoc scaling (back into Bicep within a day): the functions' `maximumInstanceCount` or the container's `maxReplicas`.
Note: more consumers mean more RU in Cosmos and more SQL connections.

## Outbox not publishing (`outbox-lag` and `outbox-malformed` alerts)

`outbox-lag`: events wait in the database for more than 2 minutes. The order is saved, but nobody knows about it.

1. Which role: `AppRoleName` in the alert (`orders-processors`, `payments-processor`, `shipping-processor`,
   `inventory-worker`).
2. Cosmos relay: does the `*-outbox-relay` function throw? An exception while publishing stops the Change Feed partition
   and retries the batch, so the lag grows but nothing is lost. Typical causes: a missing Data Sender role on the topic,
   Service Bus unavailable, the `leases` container deleted.
3. SQL relay: is `inventory-worker` running? The poller logs pending records ([outbox.kql](../kql/outbox.kql)).

`outbox-malformed`: the relay skipped a document that could not be deserialized. **This message will not be published
automatically.** The document identifier and source are in the log. Read the document in Cosmos, find the cause (usually
a schema change without backward compatibility), fix the code. The missing event is restored by writing a new outbox
record with the same content (a task for the owning team, reviewed by a second person).

## Verification

- The entity's DLQ = 0, the `sb-deadletters` alert resolved (auto-mitigate after 5 min).
- For replayed orders: [order-trace.kql](../kql/order-trace.kql) shows processing with the `handled` outcome.
- The backlog is falling: [queues.kql](../kql/queues.kql).

## Follow-up

- A message that came back to the DLQ after a replay means a code or data bug: a ticket for the owning team.
- A missing alert or query: update [alerts.bicep](../../infra/modules/operations/alerts.bicep) and [docs/kql](../kql).
