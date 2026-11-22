# order-saga

Durable Functions (isolated worker, .NET 10) on the **Flex Consumption** plan, **Durable Task Scheduler** backend.
The logic lives in the [`ShopFlow.OrderSaga`](../ShopFlow.OrderSaga) library; this project only wires it to triggers.

## Functions

| Function | Type | Role |
|---|---|---|
| `OrderSaga` | orchestrator | the saga flow ([OrderSagaWorkflow.cs](../ShopFlow.OrderSaga/Workflow/OrderSagaWorkflow.cs)) |
| `SendSagaCommand` | activity | a command to inventory/payments/shipping (Service Bus) |
| `ConfirmOrder`, `RejectOrder`, `CompleteOrderCancellation` | activities | HTTP to orders-api `/internal/orders/...` |
| `RecordSagaProgress` | activity | the projection in Cosmos DB (`saga/saga-state`) |
| `saga-start` | Service Bus, `orders-events/saga-start` | `OrderSubmitted` v2 → start of the `order-{orderId}` instance |
| `saga-replies` | Service Bus, the session-enabled queue `saga-replies` | participant replies → external events of the instance |
| `saga-status`, `saga-resolution` | HTTP (Easy Auth, role `Saga.Operator`) | the operator's view and decision |

## Steps and compensations

| # | Command | Success / failure | Timeout | Compensation (added before sending) |
|---|---|---|---|---|
| 1 | `ReserveStock` | `StockReserved` / `StockReservationFailed` | 2 min | `ReleaseStock` |
| 2 | `AuthorizePayment` (with a `PaymentId` from the saga) | `PaymentAuthorized` / `PaymentDeclined` | 15 min | `VoidPayment` |
| 3 | `CreateShipment` v1 (courier) or v2 (pickup point) | `ShipmentCreated` / `ShipmentCreationFailed` | 5 min | `CancelShipment` |
| 4 | `CapturePayment` | `PaymentCaptured` / `PaymentCaptureFailed` | 5 min | swap `VoidPayment` → `RefundPayment` |
| 5 | `ConfirmOrder` (HTTP, If-Match) | 200 / 409 (`cancellation_pending`) | durable retries | — |

The principle this table rests on: **compensations are state-tolerant**. Releasing a non-existent reservation,
cancelling a non-existent shipment and refunding an uncaptured payment succeed without effect. The saga therefore does
not need to know whether a step it got no reply for actually ran.

Customer cancellation: the `OrderCancellationRequested` event (through the `saga-replies` queue) is checked between
steps. The step in progress finishes normally, then the saga compensates everything and calls `CompleteOrderCancellation`.

When a compensation or a call to orders-api fails despite the retries, the instance moves to the
`RequiresManualIntervention` status and waits for the `OperatorResolution` event. The procedure is in
`docs/runbooks/payment-failure.md`.

## Application settings (deployed by Bicep)

| Setting | Example | Meaning |
|---|---|---|
| `DURABLE_TASK_SCHEDULER_CONNECTION_STRING` | `Endpoint=https://dts-shopflow-saga-prod-weu.westeurope.durabletask.io;Authentication=ManagedIdentity;ClientID=<client id>` | the Durable backend; **no secret**: the scheduler accepts only managed identities |
| `TASKHUB_NAME` | `order-saga` | the task hub (a logical space of instances in the scheduler); the Durable Task Data Contributor role is granted on this hub, not on the whole scheduler |
| `ServiceBus__fullyQualifiedNamespace`, `__credential`, `__clientId` | | triggers and sending commands |
| `Cosmos__accountEndpoint`, `Cosmos__DatabaseName=saga` | | the `saga-state` projection |
| `OrdersApi__BaseUrl`, `OrdersApi__Scope` | `https://app-shopflow-orders-prod-weu.azurewebsites.net/`, `api://shopflow-orders/.default` | confirming and rejecting orders |
| `Saga__PaymentTimeout` etc. | `00:15:00` | step timeouts; they go into the input of **new** instances |
| `WEBSITE_AUTH_*` (Easy Auth) | | the built-in authentication configuration for HTTP functions |

## Deployments and running instances

A payment saga can take 15 minutes, and an instance waiting for an operator even days. A deployment of a new code
version therefore finds instances half-way through, which will be replayed with the new code.

- **Safe changes:** activity logic, command content, logging.
- **Breaking changes:** the order, number or names of durable calls in the orchestrator. Replaying an old history with new code ends with a non-determinism error.

For a breaking change we raise `defaultVersion` in host.json (e.g. to `"2.0"`) and branch the orchestrator code on
`context.Version`. `versionMatchStrategy: CurrentOrOlder` makes a worker of the new version handle old instances with the
old branch. During a rolling update in Flex Consumption a worker of the old version rejects
(`versionFailureStrategy: Reject`) instances created by the new one, and they go back to the queue and reach the new worker.

Walkthroughs: [01-place-order.md](../../../../docs/walkthroughs/01-place-order.md), [02-payment-declined-compensation.md](../../../../docs/walkthroughs/02-payment-declined-compensation.md), [03-customer-cancellation.md](../../../../docs/walkthroughs/03-customer-cancellation.md).
